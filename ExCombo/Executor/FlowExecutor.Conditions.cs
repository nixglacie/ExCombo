using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static partial class FlowExecutor {
    private static bool EvaluateCondition(ComboFlow flow, FlowNode condNode) {
        // Legacy job-gauge gate.
        if (condNode.Type == NodeType.Condition) {
            var fields = JobGaugeRegistry.GetFields(flow.Job);
            if (fields == null) return false;
            foreach (var f in fields) {
                if (f.Name != condNode.ConditionField) continue;
                return ((CompareOp)condNode.ConditionCompareOp).Evaluate(f.Get(), condNode.ConditionCompareVal);
            }
            return false;
        }

        // Boolean-expression gate over wired predicate inputs.
        if (condNode.Type == NodeType.LogicCondition) return EvalLogic(flow, condNode, new HashSet<string>());

        // Held-key gate: true while the chosen key is down (only while the game has focus).
        if (condNode.Type == NodeType.KeybindCondition)
            return condNode.CheckParamId != 0 && Plugin.KeyState[(VirtualKey)condNode.CheckParamId];

        // Manual switch: persisted state, flipped in the editor or via "/excombo toggle <name>".
        if (condNode.Type == NodeType.ToggleCondition) return condNode.ToggleOn;

        // Set/reset memory over two predicate inputs.
        if (condNode.Type == NodeType.LatchCondition) return EvalLatch(flow, condNode, new HashSet<string>());

        // Parameterized condition-family gate (Status/Cooldown/Target/Player/Party/ActionHistory/
        // Gauge). Gauge defs are job-scoped, so resolve them through the job-aware lookup.
        var def = condNode.Type == NodeType.GaugeCondition
            ? ConditionCatalog.FindGauge(flow.Job, condNode.CheckField)
            : ConditionCatalog.Find(condNode.Type, condNode.CheckField);
        if (def == null) return false;
        var ctx = new CheckCtx(condNode.CheckParamId, condNode.CheckParam2, condNode.CheckTarget == 1, condNode.CheckSource == 1);
        // Target-dependent checks fail closed with no target, so a negated form (e.g. "!in melee
        // range") can't pass while untargeted regardless of CompareOp.
        bool needsTarget = def.RequiresTarget || (def.HasTarget && ctx.TargetIsCurrent);
        if (needsTarget && !TargetHelper.HasTarget()) return false;
        var value = def.Eval(ctx);
        return ((CompareOp)condNode.ConditionCompareOp).Evaluate(value, condNode.ConditionCompareVal);
    }

    // Value carried by a predicate wire leaving a gate's output port: the gate's condition value,
    // negated when the wire leaves the false port. Only gates (conditions and other Logic nodes)
    // are valid predicate sources; anything else reads false. Used by Logic inputs and the
    // editor's wire coloring.
    public static bool PredicateSignal(ComboFlow flow, FlowNode src, int fromPort) =>
        PredicateSignal(flow, src, fromPort, new HashSet<string>());

    private static bool PredicateSignal(ComboFlow flow, FlowNode src, int fromPort, HashSet<string> seen) {
        if (!FlowNode.IsGate(src.Type)) return false;
        var v = src.Type switch {
            NodeType.LogicCondition => EvalLogic(flow, src, seen),
            NodeType.LatchCondition => EvalLatch(flow, src, seen),
            _                       => EvaluateCondition(flow, src),
        };
        return fromPort == 0 ? v : !v;
    }

    // Logic gate: evaluate the boolean expression over predicate inputs. Input i reads the edge
    // wired into slot i (ToPortIndex == i) and carries that source port's PredicateSignal.
    // Unwired inputs, invalid expressions and predicate cycles all fail closed (false).
    private static bool EvalLogic(ComboFlow flow, FlowNode node, HashSet<string> seen) {
        if (!seen.Add(node.Id)) return false;                      // cycle guard
        var ast = LogicExpr.Cached(node.LogicExpr is "" ? "1 AND 2" : node.LogicExpr);
        if (ast == null || ast.MaxInput > node.LogicInputCount) return false;
        var result = ast.Eval(i => {
            var e   = flow.Edges.Find(x => x.ToNodeId == node.Id && x.ToPortIndex == i);
            var src = e != null ? flow.Nodes.Find(n => n.Id == e.FromNodeId) : null;
            return src != null && PredicateSignal(flow, src, e!.FromPortIndex, seen);
        });
        seen.Remove(node.Id);
        return result;
    }

    // ── Latch (set/reset memory) ─────────────────────────────────────────────
    // Runtime-only state, keyed "{flow.Id}:{node.Id}"; not persisted. Cleared by InvalidateFlow
    // (any editor commit) or the node's "Reset Latch State" context item.
    private static readonly Dictionary<string, bool> _latches = new();

    public static bool LatchState(ComboFlow flow, string nodeId) =>
        _latches.GetValueOrDefault($"{flow.Id}:{nodeId}");

    public static void ResetLatch(ComboFlow flow, string nodeId) =>
        _latches.Remove($"{flow.Id}:{nodeId}");

    // Slot 1 = SET, slot 2 = RESET; reset wins. The update is idempotent within a frame (signals
    // are stable across a frame), so evaluating lazily from any caller is safe.
    private static bool EvalLatch(ComboFlow flow, FlowNode node, HashSet<string> seen) {
        if (!seen.Add(node.Id)) return LatchState(flow, node.Id);   // cycle guard: read-only
        bool Signal(int slot) {
            var e   = flow.Edges.Find(x => x.ToNodeId == node.Id && x.ToPortIndex == slot);
            var src = e != null ? flow.Nodes.Find(n => n.Id == e.FromNodeId) : null;
            return src != null && PredicateSignal(flow, src, e!.FromPortIndex, seen);
        }
        var key     = $"{flow.Id}:{node.Id}";
        var latched = _latches.GetValueOrDefault(key);
        latched = Signal(2) ? false : Signal(1) || latched;
        _latches[key] = latched;
        seen.Remove(node.Id);
        return latched;
    }
}
