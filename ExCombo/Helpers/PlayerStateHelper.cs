using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ExCombo.Helpers;

// Player / combat state (from Wrath PlayerCharacter.cs, Movement.cs, Timer.cs, Resource.cs).
// Movement and combat-duration are frame-tracked; call Update() once per framework tick.
internal static class PlayerStateHelper {
    private static Vector3 _lastPos;
    private static bool    _hasPos;
    private static long    _stillSinceTick;
    private static long    _movingSinceTick;
    private static bool    _wasInCombat;
    private static long    _combatStartTick;

    public static void Update() {
        var now    = Environment.TickCount64;
        var player = Plugin.ObjectTable.LocalPlayer;

        // Movement: position delta frame-to-frame.
        if (player != null) {
            var pos = player.Position;
            if (_hasPos) {
                bool moving = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(_lastPos.X, _lastPos.Z)) > 0.01f;
                if (moving) { if (_movingSinceTick == 0) _movingSinceTick = now; _stillSinceTick = 0; }
                else        { if (_stillSinceTick  == 0) _stillSinceTick  = now; _movingSinceTick = 0; }
            }
            _lastPos = pos;
            _hasPos  = true;
        }

        // Combat duration: stamp on rising edge.
        bool inCombat = InCombat();
        if (inCombat && !_wasInCombat) _combatStartTick = now;
        if (!inCombat) _combatStartTick = 0;
        _wasInCombat = inCombat;
    }

    public static bool InCombat() => Plugin.Condition[ConditionFlag.InCombat];

    // ~last-frame movement state (true while position is changing).
    public static bool IsMoving() => _movingSinceTick != 0;

    public static float CombatTime() => _combatStartTick == 0 ? 0f : (Environment.TickCount64 - _combatStartTick) / 1000f;

    public static unsafe int LimitBreakLevel() {
        var lb = LimitBreakController.Instance();
        if (lb == null || lb->BarUnits == 0) return 0;
        return lb->CurrentUnits / lb->BarUnits;
    }

    public static bool IsOccupied() {
        var c = Plugin.Condition;
        return c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30] || c[ConditionFlag.Occupied33]
            || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39]
            || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.OccupiedInEvent]
            || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedSummoningBell]
            || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.Crafting] || c[ConditionFlag.ExecutingCraftingAction]
            || c[ConditionFlag.PreparingToCraft] || c[ConditionFlag.Unconscious]
            || c[ConditionFlag.MeldingMateria] || c[ConditionFlag.Gathering]
            || c[ConditionFlag.Mounting] || c[ConditionFlag.Mounting71]
            || c[ConditionFlag.Fishing];
    }
}
