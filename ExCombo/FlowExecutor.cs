using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
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
        // (branchId, port) selected at each level of the last resolution pass, innermost first.
        public readonly List<(string BranchId, int Port)> ActivePath = new();
        public string? LastNestedLog       = null;
        public bool    PendingFire         = false;
        public uint    FrozenReturn        = 0;
        public long    LastPressedTick     = 0;
        public float   LastSeenAnimLock    = 0f;
        public bool    SawLockDecay        = true;   // true once the anim lock returned to idle since the last accounted cast
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

                // Keep the press rising-edge baseline valid while this trigger sits idle (no polls): the
                // player animation lock is global and decays every frame, but LastSeenAnimLock is
                // otherwise only sampled on this trigger's own press/queue/fire. Without this, a trigger
                // idle through a gap keeps a stale-high baseline and the next real cast (same GCD lock
                // magnitude) fails the rising-edge test → dropped advance (e.g. Hard Slash cast twice
                // after a combo reset).
                var alNow = WeaveHelper.CurrentAnimLock;
                if (alNow <= AnimLockEpsilon) s.SawLockDecay = true;        // lock idle → next rise is a fresh cast
                if (alNow < s.LastSeenAnimLock) s.LastSeenAnimLock = alNow; // follow the decay (low-water mark)

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

    // Resolved retarget object id for the action about to fire for this trigger, if the used action
    // matches the current chain node (by raw or adjusted id) and the node has a retarget chain that
    // yields a valid, in-range candidate; null otherwise (leave the cast target unchanged).
    public static ulong? ResolveRetargetTarget(ComboFlow flow, FlowNode trigger, uint usedActionId) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state) || state.NextActionId == "") return null;
        var node = flow.Nodes.Find(n => n.Id == state.NextActionId);
        if (node is not { Type: NodeType.Action }) return null;
        if (node.RetargetPriority.Count == 0 && node.RetargetMode == 0) return null;
        if (node.ActionId == usedActionId || Adjusted(node.ActionId) == usedActionId
            || trigger.ActionId == usedActionId)
            return RetargetResolver.ResolvePriority(node, usedActionId);
        return null;
    }

    public static void NotifyPressed(ComboFlow flow, FlowNode trigger) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        Tuning.Load(flow);
        if (state.PendingFire) return; // queue still pending — ignore stray press

        // Held hotbar buttons auto-repeat UseAction(a5=0) many times per GCD; only a genuine cast
        // should advance the chain. A real cast raises the player's animation lock (then it decays),
        // so advance only on a rising edge — held-repeat polls (flat/decaying lock) are ignored,
        // leaving the icon frozen on the current step.
        //
        // The game writes a cast's animation lock a few frames AFTER UseAction returns, so a lock we
        // already accounted for (a queued Fired, or the previous press) applies late and looks like a
        // fresh rise on the next held poll — double-advancing the chain one step ahead of the game
        // combo. Gate the rising edge on an intervening decay to idle: only accept a rise once the lock
        // has returned to ~0 since the last accounted cast.
        var al = WeaveHelper.CurrentAnimLock;
        if (al <= AnimLockEpsilon) state.SawLockDecay = true;             // lock idle → a future rise is a fresh cast
        if (al <= state.LastSeenAnimLock + AnimLockEpsilon) {             // not a rise
            state.LastSeenAnimLock = al;
            return;
        }
        if (!state.SawLockDecay) {                                        // rise with no intervening decay = a late-applied accounted cast
            state.LastSeenAnimLock = al;
            return;
        }
        state.SawLockDecay     = false;
        state.LastSeenAnimLock = al;

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
        state.LastSeenAnimLock = WeaveHelper.CurrentAnimLock;
        // Don't advance — action didn't fire; player should retry
        Plugin.LogDebug($"[ExCombo][QueueFail] trigger={trigger.ActionId} unfrozen, retry same action");
    }

    public static void NotifyQueued(ComboFlow flow, FlowNode trigger, uint frozenReturn) {
        if (!_states.TryGetValue(Key(flow, trigger), out var state)) return;
        Tuning.Load(flow);
        state.LastSeenAnimLock = WeaveHelper.CurrentAnimLock;   // queue doesn't advance; keep the edge tracker in sync
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
        state.SawLockDecay    = false;   // post-queue held polls must not press-advance until the lock decays
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
        state.LastSeenAnimLock = WeaveHelper.CurrentAnimLock;   // this cast's lock; a following held a5=0 can't re-advance it
        state.SawLockDecay     = false;                         // this cast's lock applies late; ignore its rise on the next held poll
        TrackWeaveCount(flow, state);
        AdvanceState(flow, trigger, state);
        Plugin.LogDebug($"[ExCombo][Fired] trigger={trigger.ActionId} {before}→{state.NextActionId}");
    }

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

    // ── Graph walker ─────────────────────────────────────────────────────────

    // Forward flow edge from a node's output port. ToPortIndex >= 1 edges are Logic-node predicate
    // wires and never carry flow, so every traversal lookup must go through this filter.
    private static FlowEdge? FlowEdgeFrom(ComboFlow f, string nodeId, int port) =>
        f.Edges.Find(e => e.FromNodeId == nodeId && e.FromPortIndex == port && e.ToPortIndex == 0);

    private static void AdvanceState(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        if (state.CurrentBranchId != null) {
            var routeNode = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (routeNode == null || !state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
                state.CurrentBranchId    = null;
                state.ActiveBranchNodeId = null;
                state.ActivePath.Clear();
                FindInitialAction(flow, trigger, state);
                return;
            }
            // Advance the active port's progress, then re-resolve from the top (priority order).
            // Lower ports keep their progress, so a preempted chain resumes where it left off.
            var port   = state.CurrentBranchPort;
            var chain  = GetActiveChain(flow, routeNode, port);
            var newIdx = bs.GetProgress(port) + 1;
            // Chain end flowing into a trailing Branch → park past the end (progress == count);
            // resolution then routes through the trailing branch instead of wrapping to the head.
            if (newIdx >= chain.Count && ChainTrailingBranch(flow, chain) != null)
                bs.SetProgress(port, chain.Count);
            else
                bs.SetProgress(port, newIdx < chain.Count ? newIdx : 0);
            // Re-resolve from the outermost entry node so higher-priority OUTER ports can preempt;
            // the (possibly nested) inner branch keeps its port progress and resumes if selected.
            var top = state.ActiveBranchNodeId != null ? flow.Nodes.Find(n => n.Id == state.ActiveBranchNodeId) : null;
            ReResolve(flow, state, top ?? routeNode);
        } else {
            var edge = FlowEdgeFrom(flow, state.NextActionId, 0);
            if (edge == null) { FindInitialAction(flow, trigger, state); return; }
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if      (next == null || next.Type == NodeType.Trigger) FindInitialAction(flow, trigger, state);
            else if (next.Type == NodeType.Action)                  state.NextActionId = next.Id;
            else if (next.Type == NodeType.Branch)                  ResolveBranch(flow, state, next);
            else if (FlowNode.IsGate(next.Type))                    EnterCondition(flow, state, next);
        }
    }

    private static void FindInitialAction(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        state.ActivePath.Clear();   // rebuilt below when routing lands in a branch
        var edge = FlowEdgeFrom(flow, trigger.Id, 0);
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
        var portEdge  = FlowEdgeFrom(flow, routeNode.Id, port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        return firstNode != null && FlowNode.IsGate(firstNode.Type)
            ? GetPortChain(flow, firstNode.Id, 0)   // condition gate → its true-port chain
            : GetPortChain(flow, routeNode.Id, port);
    }

    // Chain + current candidate for a branch port; null if disconnected or the condition gate is
    // closed. No usability/weave gating — callers decide that.
    private static (List<FlowNode> chain, int progress, FlowNode cand)? ResolvePort(
            ComboFlow flow, FlowNode branchNode, BranchNodeState bs, int port) {
        var portEdge  = FlowEdgeFrom(flow, branchNode.Id, port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        if (firstNode == null) return null;

        List<FlowNode> chain;
        if (FlowNode.IsGate(firstNode.Type)) {
            // Mid-chain: gate already passed at entry; let the started chain finish (e.g. the
            // Continuation that follows a Burst Strike which just spent the cartridge the gate reads).
            bool midChain = port == bs.ActivePort && bs.GetProgress(port) > 0;
            if (!midChain) {
                if (!EvaluateCondition(flow, firstNode)) return null;       // gate closed at entry
                bs.SetProgress(port, 0);                                    // fresh entry → chain head
            }
            chain = GetPortChain(flow, firstNode.Id, 0);
        } else {
            chain = GetPortChain(flow, branchNode.Id, port);
        }
        if (chain.Count == 0) return null;

        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) {
            // Parked past the chain end inside a trailing Branch: surface the branch as the
            // candidate — the caller recurses into it. Otherwise wrap to the chain head as before.
            if (progress == chain.Count && ChainTrailingBranch(flow, chain) is { } trail)
                return (chain, progress, trail);
            bs.SetProgress(port, 0); progress = 0;
        }
        return (chain, progress, chain[progress]);
    }

    // Strict priority selector: walk ports top→bottom, surface the first eligible one. Higher ports
    // preempt lower ones immediately — except a committed combo group holds the branch (only a
    // strictly-higher oGCD port may weave in) until it completes.
    private static void ResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        state.ActiveBranchNodeId = branchNode.Id;
        state.ActivePath.Clear();
        // Commit pointing at a node deleted in the editor → clear before resolving.
        if (state.CommittedBranchId != null && flow.Nodes.Find(n => n.Id == state.CommittedBranchId) == null) {
            state.CommittedBranchId = null;
            state.CommittedPort     = -1;
        }
        if (!TryResolveBranch(flow, state, branchNode, new HashSet<string>())) {
            // Nothing eligible this frame — fall back to the raw trigger; keep re-evaluating.
            state.CurrentBranchId = null;
            state.NextActionId    = "";
        }
    }

    // Recursive worker: a port routing into another Branch (directly or across one passed gate)
    // resolves that branch as a nested level — CurrentBranchId ends up at the INNERMOST branch
    // owning the surfaced chain, while ActiveBranchNodeId stays the outermost entry so per-frame
    // re-evaluation preserves outer-priority preemption. Returns false without touching run state
    // so the caller falls through to its next port. `seen` breaks Branch→Branch cycles and resolves
    // a diamond-shared inner branch once per pass.
    //
    // Group commitment is per-level: CommittedBranchId is the chain-owning (inner) branch, and a
    // higher-priority OUTER port may still preempt it — the group's progress persists and resumes
    // when the branch is selected again. Nested oGCD-led inner ports don't weave into an outer
    // committed hold (CommitActive/ResolvePort see nested ports as null).
    private static bool TryResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode, HashSet<string> seen) {
        if (!seen.Add(branchNode.Id)) return false;   // cycle / already resolved this pass

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
                state.ActivePath.Add((branchNode.Id, p));
                return true;
            }
            var g = ResolvePort(flow, branchNode, bs, state.CommittedPort)!.Value;
            bs.ActivePort           = state.CommittedPort;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = state.CommittedPort;
            state.NextActionId      = g.cand.Id;
            state.ActivePath.Add((branchNode.Id, state.CommittedPort));
            return true;
        }

        for (var port = 0; port < branchNode.OutputCount; port++) {
            // Port routes into another Branch → recurse; the inner branch owns the surfaced chain.
            if (PortNestedBranch(flow, branchNode, port) is { } nested) {
                if (TryResolveBranch(flow, state, nested, seen)) {
                    bs.ActivePort = port;
                    state.ActivePath.Add((branchNode.Id, port));
                    NestedLog(state, $"[ExCombo][Branch] nested {branchNode.Id}:{port} → {nested.Id} surfaced {state.NextActionId}");
                    return true;
                }
                NestedLog(state, $"[ExCombo][Branch] nested {branchNode.Id}:{port} → {nested.Id} no eligible port, next port");
                continue;   // inner branch nothing eligible this frame — try the next port
            }

            var r = ResolvePort(flow, branchNode, bs, port);
            if (r is not { } rr) continue;

            // Chain end parked in a trailing Branch → recurse; the inner branch owns from here on
            // (until the run resets — priority nodes never "complete", so there is no way back to
            // the chain head short of a reset or a gated re-entry after preemption).
            if (rr.cand.Type == NodeType.Branch) {
                if (TryResolveBranch(flow, state, rr.cand, seen)) {
                    bs.ActivePort = port;
                    state.ActivePath.Add((branchNode.Id, port));
                    NestedLog(state, $"[ExCombo][Branch] trailing {branchNode.Id}:{port} → {rr.cand.Id} surfaced {state.NextActionId}");
                    return true;
                }
                NestedLog(state, $"[ExCombo][Branch] trailing {branchNode.Id}:{port} → {rr.cand.Id} no eligible port, next port");
                continue;
            }

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
                            state.ActivePath.Add((branchNode.Id, port));
                            return true;
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
            state.ActivePath.Add((branchNode.Id, port));
            return true;
        }

        return false;
    }

    // Nested-resolution debug lines fire every frame via ReResolve — only log on change.
    private static void NestedLog(FlowRunState state, string msg) {
        if (state.LastNestedLog == msg) return;
        state.LastNestedLog = msg;
        Plugin.LogDebug(msg);
    }

    // The inner Branch a branch port routes into — directly, or across ONE passed condition gate's
    // true port. Null when the port heads a plain action chain, is disconnected, or the gate is
    // closed (a closed gate then falls through to ResolvePort, which also rejects it). A Branch
    // after action nodes is handled separately as a trailing branch (see ChainTrailingBranch).
    private static FlowNode? PortNestedBranch(ComboFlow flow, FlowNode branchNode, int port) {
        var edge = FlowEdgeFrom(flow, branchNode.Id, port);
        var node = edge != null ? flow.Nodes.Find(n => n.Id == edge.ToNodeId) : null;
        if (node == null) return null;
        if (node.Type == NodeType.Branch) return node;
        if (!FlowNode.IsGate(node.Type) || !EvaluateCondition(flow, node)) return null;
        return PortTargetBranch(flow, node.Id, 0);
    }

    // The Branch node a port's flow edge points at directly, or null.
    private static FlowNode? PortTargetBranch(ComboFlow flow, string nodeId, int port) {
        var edge = FlowEdgeFrom(flow, nodeId, port);
        var tgt  = edge != null ? flow.Nodes.Find(n => n.Id == edge.ToNodeId) : null;
        return tgt is { Type: NodeType.Branch } ? tgt : null;
    }

    // The Branch node the chain's LAST action flows into, or null. A chain that continues into a
    // priority node hands control to it once its last action has fired (progress parks at
    // chain.Count instead of wrapping to the head).
    private static FlowNode? ChainTrailingBranch(ComboFlow flow, List<FlowNode> chain) =>
        chain.Count == 0 ? null : PortTargetBranch(flow, chain[^1].Id, 0);

    private static void EnterCondition(ComboFlow flow, FlowRunState state, FlowNode condNode) {
        state.ActiveBranchNodeId = condNode.Id;
        state.ActivePath.Clear();
        bool result = EvaluateCondition(flow, condNode);
        int  port   = result ? 0 : 1;

        // A gate port routing into a Branch resolves that branch as a nested level beneath the gate
        // (the gate stays the outermost re-entry node, so it re-evaluates every frame). The gate
        // already decided the route — an empty inner branch surfaces nothing rather than falling
        // back to the gate's other port.
        var chain = GetPortChain(flow, condNode.Id, port);
        if (chain.Count == 0) {
            if (PortTargetBranch(flow, condNode.Id, port) is { } b) { RouteConditionBranch(flow, state, b); return; }
            port ^= 1;
            chain = GetPortChain(flow, condNode.Id, port);
            if (chain.Count == 0 && PortTargetBranch(flow, condNode.Id, port) is { } b2) { RouteConditionBranch(flow, state, b2); return; }
        }
        if (chain.Count == 0) { state.NextActionId = ""; return; }

        if (!state.BranchStates.TryGetValue(condNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[condNode.Id] = bs;
        }
        if (bs.ActivePort >= 0 && bs.ActivePort != port) bs.SetProgress(bs.ActivePort, 0);
        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) {
            // Parked past the chain end inside a trailing Branch → route through it.
            if (progress == chain.Count && ChainTrailingBranch(flow, chain) is { } tb) {
                RouteConditionBranch(flow, state, tb);
                return;
            }
            bs.SetProgress(port, 0); progress = 0;
        }
        bs.ActivePort           = port;
        state.CurrentBranchId   = condNode.Id;
        state.CurrentBranchPort = port;
        state.NextActionId      = chain[progress].Id;
    }

    private static void RouteConditionBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        if (!TryResolveBranch(flow, state, branchNode, new HashSet<string>())) {
            state.CurrentBranchId = null;
            state.NextActionId    = "";   // nothing eligible this frame; re-evaluated next frame
        }
    }

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

    private static List<FlowNode> GetPortChain(ComboFlow flow, string branchNodeId, int portIndex) {
        var chain = new List<FlowNode>();
        var edge  = FlowEdgeFrom(flow, branchNodeId, portIndex);
        if (edge == null) return chain;

        var current = edge.ToNodeId;
        var visited = new HashSet<string>();
        while (!visited.Contains(current)) {
            visited.Add(current);
            var node = flow.Nodes.Find(n => n.Id == current);
            if (node is not { Type: NodeType.Action }) break;
            chain.Add(node);
            var next = FlowEdgeFrom(flow, current, 0);
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
                var top = state.ActiveBranchNodeId != null ? flow.Nodes.Find(n => n.Id == state.ActiveBranchNodeId) : null;
                ReResolve(flow, state, top ?? node);
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
            var edge = FlowEdgeFrom(flow, current, 0);
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

    // Minimum rise in the player's animation lock that counts as a new cast (vs. held-repeat noise).
    private const float AnimLockEpsilon = 0.01f;

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
        s.LastSeenAnimLock   = 0f;
        s.SawLockDecay       = true;
        s.WeavedThisWindow   = 0;
        s.LastGCDElapsed     = 0f;
        s.LastTickSkipId     = null;
        s.LastNestedLog      = null;
        s.ActivePath.Clear();
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
