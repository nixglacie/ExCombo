using System;
using System.Collections.Generic;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static class FlowExecutor {
    private sealed class FlowRunState {
        public string  NextActionId      = "";
        public string? CurrentBranchId   = null;
        public int     CurrentBranchPort = 0;
        public readonly Dictionary<string, BranchNodeState> BranchStates = new();
        public bool    PendingFire       = false;
        public uint    FrozenReturn      = 0;
        public long    LastPressedTick   = 0;
    }

    private sealed class BranchNodeState {
        public int ActivePort = -1;
        public int PortIndex  = 0;
    }

    private static readonly Dictionary<string, FlowRunState> _states = new();

    private const long ResetAfterMs = 15_000;

    // Set by ActionHook while inside UseAction(a5=1) original call.
    public static bool InQueueExecute = false;

    public static void Tick(List<ComboFlow> flows) {
        var now = Environment.TickCount64;
        foreach (var flow in flows)
            foreach (var trigger in flow.Nodes)
                if (trigger.Type == NodeType.Trigger && _states.TryGetValue(Key(flow, trigger), out var s)
                    && s.LastPressedTick > 0 && (now - s.LastPressedTick) > ResetAfterMs) {
                    Plugin.Log.Debug($"[ExCombo][Tick] trigger={trigger.ActionId} inactive {ResetAfterMs}ms, resetting");
                    ResetState(s);
                }
    }

    public static uint Resolve(ComboFlow flow, FlowNode trigger, uint triggerActionId) {
        var state = GetOrCreate(flow, trigger);

        if (state.NextActionId == "")
            FindInitialAction(flow, trigger, state);

        if (InQueueExecute && !state.PendingFire) return triggerActionId;
        if (state.PendingFire) return state.FrozenReturn;

        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        return node?.ActionId ?? triggerActionId;
    }

    public static uint GetCurrentChainAction(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.NextActionId == "") return 0;
        return flow.Nodes.Find(n => n.Id == state.NextActionId)?.ActionId ?? 0;
    }

    public static void NotifyPressed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        state.LastPressedTick = Environment.TickCount64;
        var before = state.NextActionId;
        AdvanceState(flow, trigger, state);
        Plugin.Log.Debug($"[ExCombo][Advance] trigger={trigger.ActionId} {before}→{state.NextActionId} immediate");
    }

    public static void NotifyQueued(ComboFlow flow, FlowNode trigger, uint frozenReturn) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        state.PendingFire     = true;
        state.FrozenReturn    = frozenReturn;
        state.LastPressedTick = Environment.TickCount64;
        Plugin.Log.Debug($"[ExCombo][Queue] trigger={trigger.ActionId} frozen={frozenReturn} next={state.NextActionId}");
    }

    public static void NotifyFired(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        if (!state.PendingFire) return;
        var before            = state.NextActionId;
        state.PendingFire     = false;
        state.FrozenReturn    = 0;
        state.LastPressedTick = Environment.TickCount64;
        AdvanceState(flow, trigger, state);
        Plugin.Log.Debug($"[ExCombo][Fired] trigger={trigger.ActionId} {before}→{state.NextActionId}");
    }

    public static void InvalidateFlow(string flowId) {
        var toRemove = new List<string>();
        foreach (var k in _states.Keys)
            if (k.StartsWith(flowId)) toRemove.Add(k);
        foreach (var k in toRemove) _states.Remove(k);
    }

    // ── Graph walker ─────────────────────────────────────────────────────────

    private static void AdvanceState(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        if (state.CurrentBranchId != null) {
            if (!state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
                state.CurrentBranchId = null;
                FindInitialAction(flow, trigger, state);
                return;
            }
            bs.PortIndex++;
            var portChain = GetPortChain(flow, state.CurrentBranchId, bs.ActivePort);
            if (bs.PortIndex < portChain.Count) {
                state.NextActionId = portChain[bs.PortIndex].Id;
            } else {
                bs.ActivePort         = -1;
                bs.PortIndex          = 0;
                state.CurrentBranchId = null;
                FindInitialAction(flow, trigger, state);
            }
        } else {
            var edge = flow.Edges.Find(e => e.FromNodeId == state.NextActionId && e.FromPortIndex == 0);
            if (edge == null) { FindInitialAction(flow, trigger, state); return; }
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if      (next == null || next.Type == NodeType.Trigger) FindInitialAction(flow, trigger, state);
            else if (next.Type == NodeType.Action)                  state.NextActionId = next.Id;
            else if (next.Type == NodeType.Branch)                  EnterBranch(flow, state, next);
        }
    }

    private static void FindInitialAction(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        var edge = flow.Edges.Find(e => e.FromNodeId == trigger.Id && e.FromPortIndex == 0);
        if (edge == null) { state.NextActionId = ""; return; }
        var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
        if      (next == null)                  state.NextActionId = "";
        else if (next.Type == NodeType.Action)  state.NextActionId = next.Id;
        else if (next.Type == NodeType.Branch)  EnterBranch(flow, state, next);
        else                                    state.NextActionId = "";
    }

    private static void EnterBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        if (!state.BranchStates.TryGetValue(branchNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[branchNode.Id] = bs;
        }

        var fallbackPort  = -1;
        List<FlowNode>? fallbackChain = null;

        for (var port = 0; port < branchNode.OutputCount; port++) {
            var chain = GetPortChain(flow, branchNode.Id, port);
            if (chain.Count == 0) continue;
            if (fallbackPort < 0) { fallbackPort = port; fallbackChain = chain; }
            if (IsActionUsable(chain[0].ActionId)) {
                bs.ActivePort           = port;
                bs.PortIndex            = 0;
                state.CurrentBranchId   = branchNode.Id;
                state.CurrentBranchPort = port;
                state.NextActionId      = chain[0].Id;
                return;
            }
        }

        // No port passed usability check (e.g. GCD just fired) — fall back to first connected port.
        // Priority selection still activates when cooldowns differ between ports.
        if (fallbackPort >= 0) {
            bs.ActivePort           = fallbackPort;
            bs.PortIndex            = 0;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = fallbackPort;
            state.NextActionId      = fallbackChain![0].Id;
        } else {
            state.NextActionId = "";
        }
    }

    private static List<FlowNode> GetPortChain(ComboFlow flow, string branchNodeId, int portIndex) {
        var chain = new List<FlowNode>();
        var edge  = flow.Edges.Find(e => e.FromNodeId == branchNodeId && e.FromPortIndex == portIndex);
        if (edge == null) return chain;

        var current = edge.ToNodeId;
        var visited = new HashSet<string>();
        while (!visited.Contains(current)) {
            visited.Add(current);
            var node = flow.Nodes.Find(n => n.Id == current);
            if (node is not { Type: NodeType.Action }) break;
            chain.Add(node);
            var next = flow.Edges.Find(e => e.FromNodeId == current && e.FromPortIndex == 0);
            if (next == null) break;
            current = next.ToNodeId;
        }
        return chain;
    }

    private static unsafe bool IsActionUsable(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr != null && mgr->GetActionStatus(ActionType.Action, actionId) == 0;
    }

    private static void ResetState(FlowRunState s) {
        s.NextActionId    = "";
        s.CurrentBranchId = null;
        s.PendingFire     = false;
        s.FrozenReturn    = 0;
        s.LastPressedTick = 0;
        foreach (var bs in s.BranchStates.Values) {
            bs.ActivePort = -1;
            bs.PortIndex  = 0;
        }
    }

    private static FlowRunState GetOrCreate(ComboFlow flow, FlowNode trigger) {
        var key = Key(flow, trigger);
        if (!_states.TryGetValue(key, out var state)) {
            state = new FlowRunState();
            _states[key] = state;
        }
        return state;
    }

    private static string Key(ComboFlow flow, FlowNode trigger) => $"{flow.Id}:{trigger.Id}";
}
