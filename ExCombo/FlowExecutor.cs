using System;
using System.Collections.Generic;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static class FlowExecutor {
    private sealed class FlowRunState {
        public string  NextActionId        = "";
        public string? CurrentBranchId     = null;
        public string? ActiveBranchNodeId  = null;
        public int     CurrentBranchPort   = 0;
        public readonly Dictionary<string, BranchNodeState> BranchStates = new();
        public bool    PendingFire         = false;
        public uint    FrozenReturn        = 0;
        public long    LastPressedTick     = 0;
        public int     WeavedThisWindow    = 0;
        public float   LastGCDElapsed      = 0f;
        public string? LastTickSkipId      = null;
    }

    private sealed class BranchNodeState {
        public int ActivePort = -1;
        public readonly Dictionary<int, int> PortProgress = new();

        public int  GetProgress(int port) =>
            PortProgress.TryGetValue(port, out var i) ? i : 0;
        public void SetProgress(int port, int index) {
            if (index <= 0) PortProgress.Remove(port);
            else PortProgress[port] = index;
        }
    }

    private static readonly Dictionary<string, FlowRunState> _states = new();

    private const long ResetAfterMs = 15_000;

    // Set by ActionHook while inside UseAction(a5=1) original call.
    public static bool InQueueExecute = false;

    public static void Tick(List<ComboFlow> flows) {
        var now = Environment.TickCount64;
        foreach (var flow in flows)
            foreach (var trigger in flow.Nodes) {
                if (trigger.Type != NodeType.Trigger) continue;
                if (!_states.TryGetValue(Key(flow, trigger), out var s)) continue;

                if (s.LastPressedTick > 0 && (now - s.LastPressedTick) > ResetAfterMs) {
                    Plugin.Log.Debug($"[ExCombo][Tick] trigger={trigger.ActionId} inactive {ResetAfterMs}ms, resetting");
                    ResetState(s);
                    continue;
                }

                // Detect new GCD window (elapsed went backwards) → reset weave count
                var gcdElapsed = WeaveHelper.GCDElapsed;
                if (s.WeavedThisWindow > 0 && gcdElapsed < s.LastGCDElapsed - 0.05f) {
                    Plugin.Log.Debug($"[ExCombo][Tick] trigger={trigger.ActionId} new GCD window, resetting WeavedThisWindow={s.WeavedThisWindow}→0");
                    s.WeavedThisWindow = 0;
                    s.LastTickSkipId   = null;
                }
                s.LastGCDElapsed = gcdElapsed;

                // Auto-skip oGCD nodes when window expired or max weaves (2) reached
                if (!s.PendingFire && s.NextActionId != "") {
                    for (var guard = 0; guard < 20; guard++) {
                        var cur = flow.Nodes.Find(n => n.Id == s.NextActionId);
                        if (cur?.IsOgcd != true) break;
                        bool shouldSkip = WeaveHelper.IsWeaveWindowExpired()
                                       || (WeaveHelper.IsGcdRolling && s.WeavedThisWindow >= 2);
                        if (!shouldSkip) break;
                        if (s.LastTickSkipId != cur.Id) {
                            Plugin.Log.Debug($"[ExCombo][Tick] trigger={trigger.ActionId} skipping oGCD={cur.ActionId} weaved={s.WeavedThisWindow}");
                            s.LastTickSkipId = cur.Id;
                        }
                        var prevId = s.NextActionId;
                        AdvanceState(flow, trigger, s);
                        if (s.NextActionId == "" || s.NextActionId == prevId) break;
                    }
                }
            }
    }

    public static uint Resolve(ComboFlow flow, FlowNode trigger, uint triggerActionId) {
        var state = GetOrCreate(flow, trigger);

        if (state.NextActionId == "")
            FindInitialAction(flow, trigger, state);
        else if (state.ActiveBranchNodeId != null && !state.PendingFire && !InQueueExecute) {
            // Re-evaluate every frame so the icon reflects the highest-priority eligible port.
            var active = flow.Nodes.Find(n => n.Id == state.ActiveBranchNodeId);
            if (active != null) ReResolve(flow, state, active);
        }

        if (InQueueExecute && !state.PendingFire) return triggerActionId;
        if (state.PendingFire) return state.FrozenReturn;

        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node != null && node.IsOgcd && !WeaveWindowOpen(state))
            return triggerActionId;
        return node?.ActionId ?? triggerActionId;
    }

    public static uint GetCurrentChainAction(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.NextActionId == "") return 0;
        return flow.Nodes.Find(n => n.Id == state.NextActionId)?.ActionId ?? 0;
    }

    public static void NotifyPressed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        if (state.PendingFire) return; // queue still pending — ignore stray press
        state.LastPressedTick = Environment.TickCount64;
        var before = state.NextActionId;
        TrackWeaveCount(flow, state);
        AdvanceState(flow, trigger, state);
        Plugin.Log.Debug($"[ExCombo][Advance] trigger={trigger.ActionId} {before}→{state.NextActionId} immediate");
    }

    public static void NotifyQueueFailed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        if (!state.PendingFire) return;
        state.PendingFire  = false;
        state.FrozenReturn = 0;
        // Don't advance — action didn't fire; player should retry
        Plugin.Log.Debug($"[ExCombo][QueueFail] trigger={trigger.ActionId} unfrozen, retry same action");
    }

    public static void NotifyQueued(ComboFlow flow, FlowNode trigger, uint frozenReturn) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node?.IsOgcd == true && (state.WeavedThisWindow >= 2 || WeaveHelper.IsWeaveWindowExpired())) {
            // Weave limit hit or window expired — skip oGCD and let queue fire the trigger as-is
            state.LastPressedTick = Environment.TickCount64;
            AdvanceState(flow, trigger, state);
            Plugin.Log.Debug($"[ExCombo][Queue] trigger={trigger.ActionId} oGCD blocked weaved={state.WeavedThisWindow} expired={WeaveHelper.IsWeaveWindowExpired()}, advanced to {state.NextActionId}");
            return;
        }
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
        TrackWeaveCount(flow, state);
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
            var routeNode = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (routeNode == null || !state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
                state.CurrentBranchId    = null;
                state.ActiveBranchNodeId = null;
                FindInitialAction(flow, trigger, state);
                return;
            }
            // Advance the active port's progress, then re-resolve from the top (priority order).
            // Lower ports keep their progress, so a preempted chain resumes where it left off.
            var port   = state.CurrentBranchPort;
            var chain  = GetActiveChain(flow, routeNode, port);
            var newIdx = bs.GetProgress(port) + 1;
            bs.SetProgress(port, newIdx < chain.Count ? newIdx : 0);
            ReResolve(flow, state, routeNode);
        } else {
            var edge = flow.Edges.Find(e => e.FromNodeId == state.NextActionId && e.FromPortIndex == 0);
            if (edge == null) { FindInitialAction(flow, trigger, state); return; }
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if      (next == null || next.Type == NodeType.Trigger) FindInitialAction(flow, trigger, state);
            else if (next.Type == NodeType.Action)                  state.NextActionId = next.Id;
            else if (next.Type == NodeType.Branch)                  ResolveBranch(flow, state, next);
            else if (next.Type == NodeType.Condition)               EnterCondition(flow, state, next);
        }
    }

    private static void FindInitialAction(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        var edge = flow.Edges.Find(e => e.FromNodeId == trigger.Id && e.FromPortIndex == 0);
        if (edge == null) { state.NextActionId = ""; return; }
        var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
        if      (next == null)                    state.NextActionId = "";
        else if (next.Type == NodeType.Action)    state.NextActionId = next.Id;
        else if (next.Type == NodeType.Branch)    ResolveBranch(flow, state, next);
        else if (next.Type == NodeType.Condition) EnterCondition(flow, state, next);
        else                                      state.NextActionId = "";
    }

    // Re-evaluate a Branch or standalone Condition node from scratch.
    private static void ReResolve(ComboFlow flow, FlowRunState state, FlowNode node) {
        if      (node.Type == NodeType.Branch)    ResolveBranch(flow, state, node);
        else if (node.Type == NodeType.Condition) EnterCondition(flow, state, node);
    }

    // The action chain currently routed through a branch port (resolves nested Condition gates).
    private static List<FlowNode> GetActiveChain(ComboFlow flow, FlowNode routeNode, int port) {
        if (routeNode.Type == NodeType.Condition) return GetPortChain(flow, routeNode.Id, port);
        var portEdge  = flow.Edges.Find(e => e.FromNodeId == routeNode.Id && e.FromPortIndex == port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        return firstNode?.Type == NodeType.Condition
            ? GetPortChain(flow, firstNode.Id, 0)   // condition gate → its true-port chain
            : GetPortChain(flow, routeNode.Id, port);
    }

    // Strict priority selector: walk ports top→bottom, surface the first eligible one.
    // Higher ports preempt lower ones immediately; a preempted port keeps its chain progress.
    private static void ResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        state.ActiveBranchNodeId = branchNode.Id;

        if (!state.BranchStates.TryGetValue(branchNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[branchNode.Id] = bs;
        }

        for (var port = 0; port < branchNode.OutputCount; port++) {
            var portEdge  = flow.Edges.Find(e => e.FromNodeId == branchNode.Id && e.FromPortIndex == port);
            var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
            if (firstNode == null) continue;

            List<FlowNode> chain;
            if (firstNode.Type == NodeType.Condition) {
                if (!EvaluateCondition(flow.Job, firstNode)) continue;   // gate closed → next port
                chain = GetPortChain(flow, firstNode.Id, 0);
            } else {
                chain = GetPortChain(flow, branchNode.Id, port);
            }
            if (chain.Count == 0) continue;

            var progress = bs.GetProgress(port);
            if (progress >= chain.Count) { bs.SetProgress(port, 0); progress = 0; }
            var cand = chain[progress];

            // oGCD ports are eligible only inside the weave window and when actually usable.
            if (cand.IsOgcd) {
                if (!WeaveWindowOpen(state))         continue;
                if (!IsUsableForWeave(cand.ActionId)) continue;
            }
            // GCD ports win by priority alone — the game queues until the GCD is ready.

            bs.ActivePort           = port;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = port;
            state.NextActionId      = cand.Id;
            return;
        }

        // Nothing eligible this frame — fall back to the raw trigger; keep re-evaluating.
        state.CurrentBranchId = null;
        state.NextActionId    = "";
    }

    private static void EnterCondition(ComboFlow flow, FlowRunState state, FlowNode condNode) {
        state.ActiveBranchNodeId = condNode.Id;
        bool result = EvaluateCondition(flow.Job, condNode);
        int  port   = result ? 0 : 1;
        var  chain  = GetPortChain(flow, condNode.Id, port);
        if (chain.Count == 0) { port ^= 1; chain = GetPortChain(flow, condNode.Id, port); }
        if (chain.Count == 0) { state.NextActionId = ""; return; }

        if (!state.BranchStates.TryGetValue(condNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[condNode.Id] = bs;
        }
        if (bs.ActivePort >= 0 && bs.ActivePort != port) bs.SetProgress(bs.ActivePort, 0);
        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) { bs.SetProgress(port, 0); progress = 0; }
        bs.ActivePort           = port;
        state.CurrentBranchId   = condNode.Id;
        state.CurrentBranchPort = port;
        state.NextActionId      = chain[progress].Id;
    }

    private static bool EvaluateCondition(string job, FlowNode condNode) {
        var fields = JobGaugeRegistry.GetFields(job);
        if (fields == null) return false;
        foreach (var f in fields) {
            if (f.Name != condNode.ConditionField) continue;
            return ((CompareOp)condNode.ConditionCompareOp).Evaluate(f.Get(), condNode.ConditionCompareVal);
        }
        return false;
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

    // True while a GCD is rolling, the weave budget isn't spent, and the window hasn't closed.
    private static bool WeaveWindowOpen(FlowRunState state) =>
        WeaveHelper.IsGcdRolling
        && state.WeavedThisWindow < 2
        && !WeaveHelper.IsWeaveWindowExpired();

    // Tolerance below which the oGCD's own recast is treated as "ready" — covers the brief
    // animation-lock gap after casting (a GCD or a prior weave) without over-showing on-CD oGCDs.
    private const float RecastReadyTolerance = 0.7f;

    // Usable for weaving: resolves the evolved (adjusted) id, and shows the oGCD through animation
    // locks while still hiding it when genuinely on cooldown or short on resources.
    //
    // GetActionStatus conflates resources, the oGCD's own cooldown, and the current animation lock
    // into one error code, so we split them apart:
    //   1. status == 0                       → usable right now.
    //   2. resources missing                 → genuine block (no MP/Darkside/range/level).
    //   3. resources OK but status != 0      → blocked by GCD/own-CD/anim-lock; tolerate only when
    //                                           the oGCD's OWN recast is effectively ready.
    private static unsafe bool IsUsableForWeave(uint actionId) {
        var mgr = ActionManager.Instance();
        if (mgr == null) return false;
        var adj = mgr->GetAdjustedActionId(actionId);

        if (mgr->GetActionStatus(ActionType.Action, adj) == 0)
            return true;

        // checkRecastActive:false, checkCastingActive:false → ignores cooldown, GCD, anim-lock & casts.
        var resourcesOk = mgr->GetActionStatus(ActionType.Action, adj, 0xE000_0000, false, false) == 0;
        if (!resourcesOk) return false;

        var remaining = mgr->GetRecastTime(ActionType.Action, adj)
                      - mgr->GetRecastTimeElapsed(ActionType.Action, adj);
        return remaining < RecastReadyTolerance;
    }

    private static void TrackWeaveCount(ComboFlow flow, FlowRunState state) {
        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node?.IsOgcd == true) state.WeavedThisWindow++;
        else                      state.WeavedThisWindow = 0;
        state.LastTickSkipId = null;
    }

    private static void ResetState(FlowRunState s) {
        s.NextActionId       = "";
        s.CurrentBranchId    = null;
        s.ActiveBranchNodeId = null;
        s.PendingFire        = false;
        s.FrozenReturn       = 0;
        s.LastPressedTick    = 0;
        s.WeavedThisWindow   = 0;
        s.LastGCDElapsed     = 0f;
        s.LastTickSkipId     = null;
        foreach (var bs in s.BranchStates.Values) {
            bs.ActivePort = -1;
            bs.PortProgress.Clear();
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
