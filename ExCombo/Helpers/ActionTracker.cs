using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo.Helpers;

// Records the player's own successful casts so flows can gate on action history (was-last /
// time-since / count). Fed from ActionHook's execute path with an already-adjusted id (resolved via
// the unhooked GetAdjustedActionId to avoid re-entering the detour). Per-combat counts reset on the
// combat rising edge; the last-action snapshot persists across it.
internal static unsafe class ActionTracker {
    private static uint _lastAction;      // adjusted id of the most recent cast, 0 = none
    private static readonly Dictionary<uint, long> _lastUsedTick     = new();  // adjusted id → tick
    private static readonly Dictionary<uint, int>  _countSinceCombat = new();  // adjusted id → uses

    // Eval-time adjustment runs inside the guarded detour context, so the hooked path is safe here.
    private static uint Adjusted(uint id) {
        var mgr = ActionManager.Instance();
        return mgr != null ? mgr->GetAdjustedActionId(id) : id;
    }

    // adjustedId must already be resolved (see ActionHook.UseActionDetour).
    public static void Record(uint adjustedId) {
        if (adjustedId == 0) return;
        var now = Environment.TickCount64;
        _lastAction        = adjustedId;
        _lastUsedTick[adjustedId]     = now;
        _countSinceCombat[adjustedId] = _countSinceCombat.TryGetValue(adjustedId, out var c) ? c + 1 : 1;
    }

    // Combat rising edge → wipe per-combat counts; keep last-action for cross-pull "was last" reads.
    public static void OnCombatChanged(bool inCombat) {
        if (inCombat) _countSinceCombat.Clear();
    }

    public static bool WasLast(uint actionId) => _lastAction != 0 && _lastAction == Adjusted(actionId);

    // Seconds since the action was last used; a large sentinel when never used this session (so
    // "time since > n" reads true and "< n" reads false while untouched).
    public static float TimeSinceUsed(uint actionId)
        => _lastUsedTick.TryGetValue(Adjusted(actionId), out var t)
            ? (Environment.TickCount64 - t) / 1000f
            : 9999f;

    public static int Count(uint actionId)
        => _countSinceCombat.TryGetValue(Adjusted(actionId), out var c) ? c : 0;

    // The most recent cast fell in the given ActionCategory (see ActionHelper.Category).
    public static bool LastWasCategory(uint category)
        => _lastAction != 0 && ActionHelper.Category(_lastAction) == category;
}
