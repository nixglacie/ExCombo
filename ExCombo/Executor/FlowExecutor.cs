using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static partial class FlowExecutor {
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

    // Flow-level duty scope. Empty scope = eligible everywhere (fallback). Non-empty = only inside
    // one of the listed duties.
    public static bool FlowInScope(ComboFlow flow)
        => flow.DutyScope.Count == 0 || flow.DutyScope.Contains(Helpers.ContentHelper.CurrentDutyId());

    // Flow explicitly targets the current duty (non-empty scope that contains it). Preferred over an
    // empty-scope fallback when both share a trigger.
    public static bool FlowIsSpecificMatch(ComboFlow flow)
        => flow.DutyScope.Count > 0 && flow.DutyScope.Contains(Helpers.ContentHelper.CurrentDutyId());

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
