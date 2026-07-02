using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static partial class FlowExecutor {
    // Resolve an action id to its evolved/adjusted form (e.g. Edge of Darkness → Edge of Shadow).
    private static unsafe uint Adjusted(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr != null ? mgr->GetAdjustedActionId(actionId) : actionId;
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
}
