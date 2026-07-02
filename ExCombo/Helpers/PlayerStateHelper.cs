using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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

        // Combat duration: stamp on rising edge. Also fan the edge out to the action tracker so
        // per-combat action counts reset when a fresh pull begins.
        bool inCombat = InCombat();
        if (inCombat != _wasInCombat) ActionTracker.OnCombatChanged(inCombat);
        if (inCombat && !_wasInCombat) _combatStartTick = now;
        if (!inCombat) _combatStartTick = 0;
        _wasInCombat = inCombat;
    }

    public static bool InCombat() => Plugin.Condition[ConditionFlag.InCombat];

    // ~last-frame movement state (true while position is changing).
    public static bool IsMoving() => _movingSinceTick != 0;

    // Seconds the player has been continuously moving / standing still (0 when in the other state).
    public static float TimeMoving()    => _movingSinceTick == 0 ? 0f : (Environment.TickCount64 - _movingSinceTick) / 1000f;
    public static float TimeStoodStill() => _stillSinceTick == 0 ? 0f : (Environment.TickCount64 - _stillSinceTick) / 1000f;

    public static float CombatTime() => _combatStartTick == 0 ? 0f : (Environment.TickCount64 - _combatStartTick) / 1000f;

    // Pull-countdown state (from Wrath Functions/Timer.cs). Remaining is 0 when no countdown is up.
    public static unsafe float CountdownRemaining() {
        var a = AgentCountDownSettingDialog.Instance();
        return a != null && a->Active ? MathF.Max(0f, a->TimeRemaining) : 0f;
    }
    public static unsafe bool CountdownActive() {
        var a = AgentCountDownSettingDialog.Instance();
        return a != null && a->Active;
    }

    // Bound by a duty instance (from Wrath PlayerCharacter.InDuty).
    public static unsafe bool InDuty() {
        var gm = GameMain.Instance();
        return gm != null && gm->CurrentContentFinderConditionId > 0;
    }

    // In an active FATE the player is level-eligible for (from Wrath PlayerCharacter.InFATE).
    public static unsafe bool InFATE() {
        var fm = FateManager.Instance();
        if (fm == null || fm->CurrentFate == null) return false;
        var lvl = Plugin.ObjectTable.LocalPlayer?.Level ?? 0;
        return lvl <= fm->CurrentFate->MaxLevel;
    }

    // A summoned pet (fairy/carbuncle/egi/bunshin/etc.) owned by the player is present.
    public static bool HasPetPresent() {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;
        foreach (var o in Plugin.ObjectTable)
            if (o is IBattleNpc npc && npc.OwnerId == me.GameObjectId && npc.CurrentHp > 0) return true;
        return false;
    }

    // Job tank stance is active (Iron Will / Defiance / Grit / Royal Guard / Tank Mimicry). Only one
    // applies to the current job, so a presence check over the set is sufficient.
    private static readonly uint[] _tankStances = { 79, 91, 743, 1833, 2124 };
    public static bool HasTankStance() {
        foreach (var id in _tankStances) if (StatusHelper.Present(id, false)) return true;
        return false;
    }

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
