using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static partial class FlowExecutor {
    // ── Inspection (debug overlay / condition inspector) ─────────────────

    // Live evaluation of a gate node against current game state. Used by the editor to tint gates.
    public static bool EvalGate(ComboFlow flow, FlowNode node) => EvaluateCondition(flow, node);

    // True if a path from a trigger to this node is open under current game state
    // (every gate crossed evaluates live on the port its edge leaves). Used by the editor so an
    // action gated off by a false upstream condition doesn't read as usable.
    public static bool LiveReachable(ComboFlow flow, FlowNode node)
        => LiveReachable(flow, node, new HashSet<string>());

    private static bool LiveReachable(ComboFlow flow, FlowNode node, HashSet<string> seen) {
        if (node.Type == NodeType.Trigger) return true;
        // The live queued step is on the active path at any chain depth (also gets the gold pulse).
        if (IsQueuedAction(flow, node.Id)) return true;
        if (!seen.Add(node.Id)) return false;                 // cycle guard
        var any = false;
        foreach (var e in flow.Edges) {
            if (e.ToNodeId != node.Id) continue;
            if (e.ToPortIndex != 0) continue;                   // predicate wires don't carry flow
            var from = flow.Nodes.Find(n => n.Id == e.FromNodeId);
            if (from == null) continue;
            // Chain predecessor: a later combo step isn't castable until the run advances to it, so an
            // Action→Action edge does not grant reachability — only being the queued step does (above).
            if (from.Type == NodeType.Action) continue;
            if (FlowNode.IsGate(from.Type)) {
                var pass = EvaluateCondition(flow, from);       // port 0 = true, port 1 = false
                if (e.FromPortIndex == 0 ? !pass : pass) continue;
            }
            // Branch / Trigger: pass-through (branch port selection is priority/run-state, out of
            // scope here — only Condition gates are propagated).
            if (LiveReachable(flow, from, seen)) { any = true; break; }
        }
        seen.Remove(node.Id);
        return any;
    }

    // ── Live inspector queries (read-only views over private _states) ────────
    // Keys are "{flow.Id}:{trigger.Id}", so a flow's states are those whose key starts flow.Id.

    // True if any trigger in this flow currently has this action node queued as its next press.
    public static bool IsQueuedAction(ComboFlow flow, string nodeId) {
        foreach (var kv in _states)
            if (kv.Key.StartsWith(flow.Id) && kv.Value.NextActionId == nodeId) return true;
        return false;
    }

    // True if this trigger currently has a live run state.
    public static bool TriggerActive(ComboFlow flow, string triggerId) =>
        _states.ContainsKey($"{flow.Id}:{triggerId}");

    // Currently active output port of the given branch node across the flow's states, or -1.
    // ActivePath covers every branch level of a nested resolution; CurrentBranchId covers
    // condition-owned states (EnterCondition doesn't write ActivePath).
    public static int ActiveBranchPort(ComboFlow flow, string branchNodeId) {
        foreach (var kv in _states) {
            if (!kv.Key.StartsWith(flow.Id)) continue;
            foreach (var (id, port) in kv.Value.ActivePath)
                if (id == branchNodeId) return port;
            if (kv.Value.CurrentBranchId == branchNodeId) return kv.Value.CurrentBranchPort;
        }
        return -1;
    }

    public readonly record struct TriggerDbg(
        string FlowName, string Job, uint TriggerId, string TriggerLabel, bool HasState,
        uint NextActionId, string NextLabel, bool Pending,
        int Weaved, int MaxWeaves, bool WeaveOpen, uint Spine, int BranchPort, bool Committed);

    // Per-trigger runtime snapshot for the debug overlay. Only enabled flows.
    public static List<TriggerDbg> Snapshot(List<ComboFlow> flows) {
        var list = new List<TriggerDbg>();
        foreach (var flow in flows) {
            if (!flow.Enabled) continue;
            Tuning.Load(flow);
            foreach (var trigger in flow.Nodes) {
                if (trigger.Type != NodeType.Trigger) continue;
                var has  = _states.TryGetValue(Key(flow, trigger), out var s);
                var node = has && s!.NextActionId != "" ? flow.Nodes.Find(n => n.Id == s.NextActionId) : null;
                list.Add(new TriggerDbg(
                    flow.Name, flow.Job, trigger.ActionId,
                    trigger.ActionLabel.Length > 0 ? trigger.ActionLabel : trigger.ActionId.ToString(),
                    has,
                    node?.ActionId ?? 0,
                    node?.ActionLabel ?? "—",
                    has && s!.PendingFire,
                    has ? s!.WeavedThisWindow : 0,
                    MaxWeaves,
                    has && WeaveWindowOpen(s!),
                    has ? SpineLookahead(flow, s!) : 0,
                    has ? s!.CurrentBranchPort : -1,
                    has && s!.CommittedBranchId != null));
            }
        }
        return list;
    }

    public static void InvalidateFlow(string flowId) {
        var toRemove = new List<string>();
        foreach (var k in _states.Keys)
            if (k.StartsWith(flowId)) toRemove.Add(k);
        foreach (var k in toRemove) _states.Remove(k);

        toRemove.Clear();
        foreach (var k in _latches.Keys)
            if (k.StartsWith(flowId)) toRemove.Add(k);
        foreach (var k in toRemove) _latches.Remove(k);
    }
}
