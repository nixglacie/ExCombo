using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
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
        public string? CommittedBranchId   = null;
        public int     CommittedPort       = -1;
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

    // Effective tuning for the flow currently being processed (per-flow override ?? global ?? default).
    private static long ResetAfterMs => Tuning.ResetMs;
    private static long ComboGraceMs => Tuning.GraceMs;
    private static int  MaxWeaves    => Tuning.MaxWeaves;

    // Set by ActionHook while inside UseAction(a5=1) original call.
    public static bool InQueueExecute = false;

    // True when action replacement should be off entirely: master switch, or a safety gate
    // (PvP / occupied-cutscene / out-of-combat). Checked by ActionHook and Tick.
    public static bool ReplacementSuppressed() {
        var cfg = Plugin.Config;
        if (cfg == null) return false;
        if (!cfg.Enabled) return true;
        // Not safely in-world (login/logout, loading screens, zoning): the player and action data
        // are transitional, so evaluating flows here can dereference unloaded game memory and hard-
        // crash. Never touch anything until a local player exists and we're not between areas.
        if (Plugin.ObjectTable.LocalPlayer == null) return true;
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return true;
        if (cfg.DisableInPvP && Plugin.ClientState.IsPvP) return true;
        if (cfg.PauseWhenOccupied && Helpers.PlayerStateHelper.IsOccupied()) return true;
        if (cfg.ReplaceOnlyInCombat && !Helpers.PlayerStateHelper.InCombat()) return true;
        return false;
    }

    public static void Tick(List<ComboFlow> flows) {
        if (ReplacementSuppressed()) return;   // master switch off or a safety gate → no progression
        var now = Environment.TickCount64;
        PlayerStateHelper.Update();   // frame-track movement & combat duration for condition checks
        foreach (var flow in flows) {
            Tuning.Load(flow);
            foreach (var trigger in flow.Nodes) {
                if (trigger.Type != NodeType.Trigger) continue;
                if (!_states.TryGetValue(Key(flow, trigger), out var s)) continue;

                // Stay alive while the game's combo is still running (matches the real combo window).
                if (s.LastPressedTick > 0 && (now - s.LastPressedTick) > ResetAfterMs && ComboHelper.ComboTimer <= 0) {
                    Plugin.LogDebug($"[ExCombo][Tick] trigger={trigger.ActionId} inactive {ResetAfterMs}ms, resetting");
                    ResetState(s);
                    continue;
                }

                // Detect new GCD window (elapsed went backwards) → reset weave count
                var gcdElapsed = WeaveHelper.GCDElapsed;
                if (s.WeavedThisWindow > 0 && gcdElapsed < s.LastGCDElapsed - 0.05f) {
                    Plugin.LogDebug($"[ExCombo][Tick] trigger={trigger.ActionId} new GCD window, resetting WeavedThisWindow={s.WeavedThisWindow}→0");
                    s.WeavedThisWindow = 0;
                    s.LastTickSkipId   = null;
                }
                s.LastGCDElapsed = gcdElapsed;

                // Combo sync: if the next step is a combo continuation the game no longer supports
                // (timer expired or combo broken by another action), reset this chain to its start.
                if (!s.PendingFire && s.NextActionId != "" && (now - s.LastPressedTick) > ComboGraceMs) {
                    var step = flow.Nodes.Find(n => n.Id == s.NextActionId);
                    if (step is { Type: NodeType.Action }) {
                        var parent = ComboHelper.GetComboParent(step.ActionId);
                        if (parent != 0) {
                            // Only enforce game-combo continuity where the graph actually wires this
                            // combo step (predecessor action == the prerequisite). Otherwise the graph
                            // is authoritative and we follow it as-is.
                            FlowNode? prevAction = null;
                            foreach (var e in flow.Edges) {
                                if (e.ToNodeId != step.Id) continue;
                                var p = flow.Nodes.Find(n => n.Id == e.FromNodeId);
                                if (p is { Type: NodeType.Action }) { prevAction = p; break; }
                            }
                            bool graphModelsCombo = prevAction != null
                                && (prevAction.ActionId == parent
                                    || Adjusted(prevAction.ActionId) == parent
                                    || prevAction.ActionId == Adjusted(parent));
                            if (graphModelsCombo) {
                                var ga = ComboHelper.ComboAction;
                                bool supported = ComboHelper.ComboTimer > 0 && (ga == parent || ga == Adjusted(parent));
                                if (!supported) {
                                    Plugin.LogDebug($"[ExCombo][Combo] trigger={trigger.ActionId} desync (need {parent}, game={ga} t={ComboHelper.ComboTimer:F1}) → reset chain");
                                    ResetActiveChain(flow, trigger, s);
                                }
                            }
                        }
                    }
                }

                // Auto-skip oGCD nodes when window expired or max weaves (2) reached
                if (!s.PendingFire && s.NextActionId != "") {
                    for (var guard = 0; guard < 20; guard++) {
                        var cur = flow.Nodes.Find(n => n.Id == s.NextActionId);
                        if (cur == null || !ActionHelper.IsOgcd(cur.ActionId)) break;
                        if (OgcdOffer(s, cur)) break;   // still weaveable (or in grace) → hold
                        if (s.LastTickSkipId != cur.Id) {
                            Plugin.LogDebug($"[ExCombo][Tick] trigger={trigger.ActionId} skipping oGCD={cur.ActionId} weaved={s.WeavedThisWindow}");
                            s.LastTickSkipId = cur.Id;
                        }
                        var prevId = s.NextActionId;
                        AdvanceState(flow, trigger, s);
                        if (s.NextActionId == "" || s.NextActionId == prevId) break;
                    }
                }
            }
        }
    }

    public static uint Resolve(ComboFlow flow, FlowNode trigger, uint triggerActionId) {
        Tuning.Load(flow);
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
        if (node != null && ActionHelper.IsOgcd(node.ActionId)) {
            if (OgcdOffer(state, node)) return node.ActionId;          // weaveable → offer it
            var spine = SpineLookahead(flow, state);                  // else show the next GCD
            return spine != 0 ? spine : triggerActionId;
        }
        return node?.ActionId ?? triggerActionId;
    }

    public static uint GetCurrentChainAction(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.NextActionId == "") return 0;
        return flow.Nodes.Find(n => n.Id == state.NextActionId)?.ActionId ?? 0;
    }

    // RetargetMode of the action about to fire for this trigger, if the used action matches the
    // current chain node (by raw or adjusted id); 0 (None) otherwise.
    public static int GetRetargetForUsedAction(ComboFlow flow, FlowNode trigger, uint usedActionId) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.NextActionId == "") return 0;
        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node is not { Type: NodeType.Action } || node.RetargetMode == 0) return 0;
        if (node.ActionId == usedActionId || Adjusted(node.ActionId) == usedActionId
            || trigger.ActionId == usedActionId)
            return node.RetargetMode;
        return 0;
    }

    public static void NotifyPressed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        Tuning.Load(flow);
        if (state.PendingFire) return; // queue still pending — ignore stray press
        state.LastPressedTick = Environment.TickCount64;
        var before = state.NextActionId;
        TrackWeaveCount(flow, state);
        AdvanceState(flow, trigger, state);
        Plugin.LogDebug($"[ExCombo][Advance] trigger={trigger.ActionId} {before}→{state.NextActionId} immediate");
    }

    public static void NotifyQueueFailed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        if (!state.PendingFire) return;
        state.PendingFire  = false;
        state.FrozenReturn = 0;
        // Don't advance — action didn't fire; player should retry
        Plugin.LogDebug($"[ExCombo][QueueFail] trigger={trigger.ActionId} unfrozen, retry same action");
    }

    public static void NotifyQueued(ComboFlow flow, FlowNode trigger, uint frozenReturn) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        Tuning.Load(flow);
        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node != null && ActionHelper.IsOgcd(node.ActionId) && !OgcdOffer(state, node)) {
            // oGCD not weaveable (CD / proc down / window closed / weave limit) — skip it (and any
            // consecutive oGCDs) and let the queue fire the next GCD spine as-is. Evaluate grace
            // against the prior cast before stamping LastPressedTick.
            for (var guard = 0; guard < 20; guard++) {
                var cur = flow.Nodes.Find(n => n.Id == state.NextActionId);
                if (cur == null || !ActionHelper.IsOgcd(cur.ActionId) || OgcdOffer(state, cur)) break;
                var prevId = state.NextActionId;
                AdvanceState(flow, trigger, state);
                if (state.NextActionId == "" || state.NextActionId == prevId) break;
            }
            state.LastPressedTick = Environment.TickCount64;
            Plugin.LogDebug($"[ExCombo][Queue] trigger={trigger.ActionId} oGCD skipped, advanced to {state.NextActionId}");
            return;
        }
        state.PendingFire     = true;
        state.FrozenReturn    = frozenReturn;
        state.LastPressedTick = Environment.TickCount64;
        Plugin.LogDebug($"[ExCombo][Queue] trigger={trigger.ActionId} frozen={frozenReturn} next={state.NextActionId}");
    }

    public static void NotifyFired(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        Tuning.Load(flow);
        if (!state.PendingFire) return;
        var before            = state.NextActionId;
        state.PendingFire     = false;
        state.FrozenReturn    = 0;
        state.LastPressedTick = Environment.TickCount64;
        TrackWeaveCount(flow, state);
        AdvanceState(flow, trigger, state);
        Plugin.LogDebug($"[ExCombo][Fired] trigger={trigger.ActionId} {before}→{state.NextActionId}");
    }

    // ── Inspection (debug overlay / condition inspector) ─────────────────

    // Live evaluation of a gate node against current game state. Used by the editor to tint gates.
    public static bool EvalGate(ComboFlow flow, FlowNode node) => EvaluateCondition(flow.Job, node);

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
    public static int ActiveBranchPort(ComboFlow flow, string branchNodeId) {
        foreach (var kv in _states)
            if (kv.Key.StartsWith(flow.Id) && kv.Value.CurrentBranchId == branchNodeId)
                return kv.Value.CurrentBranchPort;
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
            else if (FlowNode.IsGate(next.Type))                    EnterCondition(flow, state, next);
        }
    }

    private static void FindInitialAction(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        var edge = flow.Edges.Find(e => e.FromNodeId == trigger.Id && e.FromPortIndex == 0);
        if (edge == null) { state.NextActionId = ""; return; }
        var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
        if      (next == null)                  state.NextActionId = "";
        else if (next.Type == NodeType.Action)  state.NextActionId = next.Id;
        else if (next.Type == NodeType.Branch)  ResolveBranch(flow, state, next);
        else if (FlowNode.IsGate(next.Type))    EnterCondition(flow, state, next);
        else                                    state.NextActionId = "";
    }

    // Re-evaluate a Branch or standalone Condition node from scratch.
    private static void ReResolve(ComboFlow flow, FlowRunState state, FlowNode node) {
        if      (node.Type == NodeType.Branch) ResolveBranch(flow, state, node);
        else if (FlowNode.IsGate(node.Type))   EnterCondition(flow, state, node);
    }

    // The action chain currently routed through a branch port (resolves nested Condition gates).
    private static List<FlowNode> GetActiveChain(ComboFlow flow, FlowNode routeNode, int port) {
        if (FlowNode.IsGate(routeNode.Type)) return GetPortChain(flow, routeNode.Id, port);
        var portEdge  = flow.Edges.Find(e => e.FromNodeId == routeNode.Id && e.FromPortIndex == port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        return firstNode != null && FlowNode.IsGate(firstNode.Type)
            ? GetPortChain(flow, firstNode.Id, 0)   // condition gate → its true-port chain
            : GetPortChain(flow, routeNode.Id, port);
    }

    // Chain + current candidate for a branch port; null if disconnected or the condition gate is
    // closed. No usability/weave gating — callers decide that.
    private static (List<FlowNode> chain, int progress, FlowNode cand)? ResolvePort(
            ComboFlow flow, FlowNode branchNode, BranchNodeState bs, int port) {
        var portEdge  = flow.Edges.Find(e => e.FromNodeId == branchNode.Id && e.FromPortIndex == port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        if (firstNode == null) return null;

        List<FlowNode> chain;
        if (FlowNode.IsGate(firstNode.Type)) {
            // Mid-chain: gate already passed at entry; let the started chain finish (e.g. the
            // Continuation that follows a Burst Strike which just spent the cartridge the gate reads).
            bool midChain = port == bs.ActivePort && bs.GetProgress(port) > 0;
            if (!midChain) {
                if (!EvaluateCondition(flow.Job, firstNode)) return null;   // gate closed at entry
                bs.SetProgress(port, 0);                                    // fresh entry → chain head
            }
            chain = GetPortChain(flow, firstNode.Id, 0);
        } else {
            chain = GetPortChain(flow, branchNode.Id, port);
        }
        if (chain.Count == 0) return null;

        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) { bs.SetProgress(port, 0); progress = 0; }
        return (chain, progress, chain[progress]);
    }

    // Strict priority selector: walk ports top→bottom, surface the first eligible one. Higher ports
    // preempt lower ones immediately — except a committed combo group holds the branch (only a
    // strictly-higher oGCD port may weave in) until it completes.
    private static void ResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        state.ActiveBranchNodeId = branchNode.Id;

        if (!state.BranchStates.TryGetValue(branchNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[branchNode.Id] = bs;
        }

        // A group port is "committed" once we've advanced PAST its first node (the previous chain
        // step shares the same group). Group entry stays priority-governed, even with a loose prefix.
        bool CommitActive(int port) {
            var r = ResolvePort(flow, branchNode, bs, port);
            if (r is not { } rr || rr.cand.GroupId == null || rr.progress == 0) return false;
            return rr.chain[rr.progress - 1].GroupId == rr.cand.GroupId;
        }
        if (state.CommittedBranchId == branchNode.Id && state.CommittedPort >= 0 && !CommitActive(state.CommittedPort)) {
            state.CommittedPort     = -1;
            state.CommittedBranchId = null;
        }
        if (state.CommittedPort < 0 && state.CurrentBranchId == branchNode.Id && CommitActive(state.CurrentBranchPort)) {
            state.CommittedPort     = state.CurrentBranchPort;
            state.CommittedBranchId = branchNode.Id;
        }

        // Committed group holds the branch — only a strictly-higher oGCD port may weave in.
        if (state.CommittedBranchId == branchNode.Id && state.CommittedPort >= 0) {
            for (var p = 0; p < state.CommittedPort; p++) {
                var rp = ResolvePort(flow, branchNode, bs, p);
                if (rp is not { } w || !ActionHelper.IsOgcd(w.cand.ActionId)) continue;
                if (!WeaveWindowOpen(state) || !IsUsableForWeave(w.cand.ActionId)) continue;
                bs.ActivePort           = p;
                state.CurrentBranchId   = branchNode.Id;
                state.CurrentBranchPort = p;
                state.NextActionId      = w.cand.Id;
                return;
            }
            var g = ResolvePort(flow, branchNode, bs, state.CommittedPort)!.Value;
            bs.ActivePort           = state.CommittedPort;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = state.CommittedPort;
            state.NextActionId      = g.cand.Id;
            return;
        }

        for (var port = 0; port < branchNode.OutputCount; port++) {
            var r = ResolvePort(flow, branchNode, bs, port);
            if (r is not { } rr) continue;

            // oGCD ports are eligible only inside the weave window and when actually usable.
            if (ActionHelper.IsOgcd(rr.cand.ActionId)) {
                // A mid-chain oGCD continuation (e.g. Hypervelocity after Burst Strike) is enabled by
                // a buff the game applies a frame late. Within the grace window hold it instead of
                // letting a lower-priority port preempt it; otherwise honour usability as normal.
                bool midChain    = port == bs.ActivePort && rr.progress > 0;
                bool withinGrace = Environment.TickCount64 - state.LastPressedTick <= ComboGraceMs;
                bool offerable   = WeaveWindowOpen(state)
                                && (IsUsableForWeave(rr.cand.ActionId) || (midChain && withinGrace));
                if (!offerable) {
                    // A mid-chain inline oGCD we can't weave must not block the spine or surrender
                    // priority — skip it (and any consecutive oGCDs) and surface the next GCD in THIS
                    // chain, keeping the port active. Fresh oGCD-led ports still drop as before.
                    if (midChain) {
                        var i = rr.progress;
                        while (i < rr.chain.Count && ActionHelper.IsOgcd(rr.chain[i].ActionId)) i++;
                        if (i < rr.chain.Count) {
                            Plugin.LogDebug($"[ExCombo][Branch] skip oGCD={rr.cand.ActionId} → next={rr.chain[i].ActionId}");
                            bs.SetProgress(port, i);
                            bs.ActivePort           = port;
                            state.CurrentBranchId   = branchNode.Id;
                            state.CurrentBranchPort = port;
                            state.NextActionId      = rr.chain[i].Id;
                            return;
                        }
                    }
                    continue;   // fresh oGCD-led port not ready, or no GCD left in chain → lower priority
                }
            }
            // GCD ports win by priority alone — the game queues until the GCD is ready.

            bs.ActivePort           = port;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = port;
            state.NextActionId      = rr.cand.Id;
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
        // Legacy job-gauge gate.
        if (condNode.Type == NodeType.Condition) {
            var fields = JobGaugeRegistry.GetFields(job);
            if (fields == null) return false;
            foreach (var f in fields) {
                if (f.Name != condNode.ConditionField) continue;
                return ((CompareOp)condNode.ConditionCompareOp).Evaluate(f.Get(), condNode.ConditionCompareVal);
            }
            return false;
        }

        // Parameterized condition-family gate (Status/Cooldown/Target/Player/Party).
        var def = ConditionCatalog.Find(condNode.Type, condNode.CheckField);
        if (def == null) return false;
        var ctx = new CheckCtx(condNode.CheckParamId, condNode.CheckParam2, condNode.CheckTarget == 1);
        // Target-dependent checks fail closed with no target, so a negated form (e.g. "!in melee
        // range") can't pass while untargeted regardless of CompareOp.
        bool needsTarget = def.RequiresTarget || (def.HasTarget && ctx.TargetIsCurrent);
        if (needsTarget && !TargetHelper.HasTarget()) return false;
        var value = def.Eval(ctx);
        return ((CompareOp)condNode.ConditionCompareOp).Evaluate(value, condNode.ConditionCompareVal);
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

    // Resolve an action id to its evolved/adjusted form (e.g. Edge of Darkness → Edge of Shadow).
    private static unsafe uint Adjusted(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr != null ? mgr->GetAdjustedActionId(actionId) : actionId;
    }

    // Reset only the active chain back to its first step, leaving branch/condition/weave state intact.
    private static void ResetActiveChain(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        if (state.CurrentBranchId != null
            && state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
            var node = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (node != null) {
                // Break inside a combo group → restart at the group's first node, not the chain head.
                var chain   = GetActiveChain(flow, node, state.CurrentBranchPort);
                var prog    = bs.GetProgress(state.CurrentBranchPort);
                var resetTo = 0;
                if (prog > 0 && prog < chain.Count && chain[prog].GroupId is { } gid) {
                    resetTo = prog;
                    while (resetTo > 0 && chain[resetTo - 1].GroupId == gid) resetTo--;
                }
                bs.SetProgress(state.CurrentBranchPort, resetTo);
                ReResolve(flow, state, node);
                return;
            }
        }
        FindInitialAction(flow, trigger, state);                  // linear chain → chain head
    }

    // True while a GCD is rolling, the weave budget isn't spent, and the window hasn't closed.
    private static bool WeaveWindowOpen(FlowRunState state) =>
        WeaveHelper.IsGcdRolling
        && state.WeavedThisWindow < MaxWeaves
        && !WeaveHelper.IsWeaveWindowExpired();

    // Should this inline oGCD still be offered (shown/fired) this frame? It's an overlay on the GCD
    // spine: weave it only inside the window when actually usable, with a short grace after the prior
    // cast so a buff applied a frame late (e.g. Hypervelocity's Ready To Blast after Burst Strike)
    // isn't skipped. Otherwise the spine falls through to the next GCD — the oGCD never blocks.
    private static bool OgcdOffer(FlowRunState state, FlowNode node) {
        if (!WeaveWindowOpen(state)) return false;
        if (IsUsableForWeave(node.ActionId)) return true;
        return Environment.TickCount64 - state.LastPressedTick <= ComboGraceMs;
    }

    // Peek the next non-oGCD (GCD) action past the current cursor without mutating state; 0 if none.
    private static uint SpineLookahead(ComboFlow flow, FlowRunState state) {
        if (state.CurrentBranchId != null) {
            var routeNode = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (routeNode == null || !state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) return 0;
            var chain = GetActiveChain(flow, routeNode, state.CurrentBranchPort);
            for (var i = bs.GetProgress(state.CurrentBranchPort) + 1; i < chain.Count; i++)
                if (!ActionHelper.IsOgcd(chain[i].ActionId)) return chain[i].ActionId;
            return 0;
        }
        var current = state.NextActionId;
        var visited = new HashSet<string>();
        while (current != "" && visited.Add(current)) {
            var edge = flow.Edges.Find(e => e.FromNodeId == current && e.FromPortIndex == 0);
            if (edge == null) return 0;
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if (next is not { Type: NodeType.Action }) return 0;
            if (!ActionHelper.IsOgcd(next.ActionId)) return next.ActionId;
            current = next.Id;
        }
        return 0;
    }

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
        var adj = Adjusted(actionId);

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
        if (node != null && ActionHelper.IsOgcd(node.ActionId)) state.WeavedThisWindow++;
        else                      state.WeavedThisWindow = 0;
        state.LastTickSkipId = null;
    }

    private static void ResetState(FlowRunState s) {
        s.NextActionId       = "";
        s.CurrentBranchId    = null;
        s.ActiveBranchNodeId = null;
        s.CommittedBranchId  = null;
        s.CommittedPort      = -1;
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

// Effective weave/combo tuning for the flow currently under evaluation. Loaded per-flow so a flow's
// own overrides win over the global Configuration, which wins over the built-in defaults.
internal static class Tuning {
    public static int   MaxWeaves = 2;
    public static float AnimLock  = 0.6f;
    public static float Queue     = 0.5f;
    public static int   GraceMs   = 500;
    public static long  ResetMs   = 15_000;

    public static void Load(ComboFlow f) {
        var c = Plugin.Config;
        MaxWeaves = f.MaxWeavesPerGcd   ?? c?.MaxWeavesPerGcd   ?? 2;
        AnimLock  = f.AnimLockBudget    ?? c?.AnimLockBudget    ?? 0.6f;
        Queue     = f.QueueBudget       ?? c?.QueueBudget       ?? 0.5f;
        GraceMs   = f.ComboGraceMs      ?? c?.ComboGraceMs      ?? 500;
        ResetMs   = (f.ChainResetSeconds ?? c?.ChainResetSeconds ?? 15) * 1000L;
    }
}
