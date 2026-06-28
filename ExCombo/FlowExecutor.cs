using System.Collections.Generic;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static class FlowExecutor {
    private sealed class FlowRunState {
        public List<FlowNode> Chain        = [];
        public int            Index        = 0;
        public uint           QueuedAction = 0;
    }

    // Key = "flowId:triggerId" — one state per trigger node, not per flow.
    private static readonly Dictionary<string, FlowRunState> _states         = new();
    private static          float                            _prevComboTimer = 0f;

    // Set by ActionHook before calling UseAction original when a5==1.
    // Tells Resolve to return QueuedAction instead of chain[Index].
    public static bool InQueueExecute = false;

    public static unsafe void Tick(List<ComboFlow> flows) {
        var timer = ActionManager.Instance()->Combo.Timer;

        if (_prevComboTimer > 0f && timer <= 0f) {
            foreach (var flow in flows)
                foreach (var trigger in flow.Nodes)
                    if (trigger.Type == NodeType.Trigger && _states.TryGetValue(Key(flow, trigger), out var s)) {
                        s.Index        = 0;
                        s.QueuedAction = 0;
                    }
        }

        _prevComboTimer = timer;
    }

    public static uint Resolve(ComboFlow flow, FlowNode trigger, uint triggerActionId) {
        var chain = GetChain(flow, trigger);
        if (chain.Count == 0) return triggerActionId;

        var state = GetOrCreate(flow, trigger, chain);

        if (InQueueExecute) {
            // Only override when WE queued this action via direct button press.
            // If QueuedAction==0 another chain's action happens to share this trigger's
            // ActionId — let the original action pass through unchanged.
            return state.QueuedAction != 0 ? state.QueuedAction : triggerActionId;
        }

        return chain[state.Index].ActionId;
    }

    public static uint GetCurrentChainAction(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.Chain.Count == 0) return 0;
        var idx = state.Index >= state.Chain.Count ? 0 : state.Index;
        return state.Chain[idx].ActionId;
    }

    public static void NotifyPressed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        var before = state.Index;
        state.Index = (state.Index + 1) % state.Chain.Count;
        Plugin.Log.Debug($"[ExCombo][Advance] trigger={trigger.ActionId} {before}→{state.Index} (chain={state.Chain.Count})");
    }

    public static void SaveQueuedAction(ComboFlow flow, FlowNode trigger, uint action) {
        if (_states.TryGetValue(Key(flow, trigger), out var state)) {
            state.QueuedAction = action;
            Plugin.Log.Debug($"[ExCombo][Queue] trigger={trigger.ActionId} saved={action}");
        }
    }

    public static void ClearQueuedAction(ComboFlow flow, FlowNode trigger) {
        if (_states.TryGetValue(Key(flow, trigger), out var state)) {
            Plugin.Log.Debug($"[ExCombo][Queue] trigger={trigger.ActionId} cleared");
            state.QueuedAction = 0;
        }
    }

    public static void InvalidateFlow(string flowId) {
        var toRemove = new List<string>();
        foreach (var k in _states.Keys)
            if (k.StartsWith(flowId)) toRemove.Add(k);
        foreach (var k in toRemove) _states.Remove(k);
    }

    private static FlowRunState GetOrCreate(ComboFlow flow, FlowNode trigger, List<FlowNode> chain) {
        var key = Key(flow, trigger);
        if (!_states.TryGetValue(key, out var state)) {
            state = new FlowRunState();
            _states[key] = state;
        }
        state.Chain = chain;
        if (state.Index >= chain.Count) state.Index = 0;
        return state;
    }

    private static string Key(ComboFlow flow, FlowNode trigger) => $"{flow.Id}:{trigger.Id}";

    private static List<FlowNode> GetChain(ComboFlow flow, FlowNode trigger) {
        var chain   = new List<FlowNode>();
        var current = trigger.Id;
        var visited = new HashSet<string>();

        while (!visited.Contains(current)) {
            visited.Add(current);
            var edge = flow.Edges.Find(e => e.FromNodeId == current);
            if (edge == null) break;
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if (next is not { Type: NodeType.Action }) break;
            chain.Add(next);
            current = next.Id;
        }
        return chain;
    }
}
