using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace ExCombo.Helpers;

public static class CharacterState {
    public static bool IsInCombat() =>
        Plugin.Condition[ConditionFlag.InCombat];

    public static bool IsWeaponDrawn() {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p != null && p.StatusFlags.HasFlag(StatusFlags.WeaponOut);
    }

    public static Job GetCharacterJob() {
        var p = Plugin.ObjectTable.LocalPlayer;
        if (p == null) return Job.UKN;
        unsafe { return (Job)((Character*)p.Address)->CharacterData.ClassJob; }
    }

    public static int GetCharacterLevel() => Plugin.ObjectTable.LocalPlayer?.Level ?? 0;

    public static bool IsInPvP() => Plugin.ClientState.IsPvP;
}
