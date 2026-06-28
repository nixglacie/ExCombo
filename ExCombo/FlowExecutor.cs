using System;
using System.Collections.Generic;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static class FlowExecutor {
    private sealed class FlowRunState {
        public List<FlowNode> Chain        = [];
        public int            Index        = 0;
        public bool           PendingFire  = false;
        public uint           FrozenReturn = 0;
        public long           LastPressedTick = 0; // Environment.TickCount64 at last press
    }

    private static readonly Dictionary<string, FlowRunState> _states = new();

    private const long ResetAfterMs = 15_000;

    // Set by ActionHook while inside UseAction(a5=1) original call.
    // Lets Resolve pass through unrelated triggers without replacing them.
    public static bool InQueueExecute = false;

    // Called every frame. Resets each trigger independently after ResetAfterMs of inactivity.
    public static void Tick(List<ComboFlow> flows) {
        var now = Environment.TickCount64;
        foreach (var flow in flows)
            foreach (var trigger in flow.Nodes)
                if (trigger.Type == NodeType.Trigger && _states.TryGetValue(Key(flow, trigger), out var s)
                    && s.LastPressedTick > 0 && (now - s.LastPressedTick) > ResetAfterMs) {
                    Plugin.Log.Debug($"[ExCombo][Tick] trigger={trigger.ActionId} inactive {ResetAfterMs}ms, resetting");
                    s.Index           = 0;
                    s.PendingFire     = false;
                    s.FrozenReturn    = 0;
                    s.LastPressedTick = 0;
                }
    }

    public static uint Resolve(ComboFlow flow, FlowNode trigger, uint triggerActionId) {
        var chain = GetChain(flow, trigger);
        if (chain.Count == 0) return triggerActionId;

        var state = GetOrCreate(flow, trigger, chain);

        // During a5=1: if this trigger has no pending queued action, pass through.
        // This prevents double-replacement when chain[N] happens to be another trigger.
        if (InQueueExecute && !state.PendingFire) return triggerActionId;

        // Freeze return value while a queued action is pending.
        // Prevents frame-by-frame drift after Index advanced but before action fires.
        if (state.PendingFire) return state.FrozenReturn;

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
        state.Index           = (state.Index + 1) % state.Chain.Count;
        state.LastPressedTick = Environment.TickCount64;
        Plugin.Log.Debug($"[ExCombo][Advance] trigger={trigger.ActionId} {before}→{state.Index} (chain={state.Chain.Count}) immediate");
    }

    public static void NotifyQueued(ComboFlow flow, FlowNode trigger, uint frozenReturn) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        state.PendingFire     = true;
        state.FrozenReturn    = frozenReturn;
        state.LastPressedTick = Environment.TickCount64;
        Plugin.Log.Debug($"[ExCombo][Queue] trigger={trigger.ActionId} frozen={frozenReturn} idx={state.Index}");
    }

    // No-op if PendingFire=false — prevents spurious advance of unrelated triggers.
    public static void NotifyFired(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        if (!state.PendingFire) return;
        var before = state.Index;
        state.Index           = (state.Index + 1) % state.Chain.Count;
        state.PendingFire     = false;
        state.FrozenReturn    = 0;
        state.LastPressedTick = Environment.TickCount64;
        Plugin.Log.Debug($"[ExCombo][Fired] trigger={trigger.ActionId} {before}→{state.Index} (chain={state.Chain.Count})");
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
