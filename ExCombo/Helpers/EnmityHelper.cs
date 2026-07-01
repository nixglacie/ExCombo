using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace ExCombo.Helpers;

// Aggro check (from Wrath Functions/Hate.cs, reimplemented without UIState offset reads).
internal static class EnmityHelper {
    // True if any hostile battle NPC currently targets the local player.
    public static bool PlayerHasAggro() {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return false;
        foreach (var o in Plugin.ObjectTable) {
            if (o is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5 /* Enemy */) continue;
            if (npc.CurrentHp == 0) continue;
            if (npc.TargetObjectId == me.GameObjectId) return true;
        }
        return false;
    }
}
