using ExCombo.Flow;
using Dalamud.Game.ClientState.Objects.Types;

namespace ExCombo.Helpers;

// Built-in target resolvers for Action-node retargeting (Phase 3, from Wrath ActionRetargeting).
// Returns the GameObjectId to redirect the action to, or null to leave the cast target unchanged.
internal static class RetargetResolver {
    public static ulong? Resolve(RetargetMode mode) => mode switch {
        RetargetMode.Self           => Plugin.ObjectTable.LocalPlayer?.GameObjectId,
        RetargetMode.LowestHpAlly   => PartyHelper.LowestHp()?.GameObjectId,
        RetargetMode.DeadMember     => PartyHelper.FirstDead()?.GameObjectId,
        RetargetMode.TargetOfTarget => (Plugin.TargetManager.Target as IBattleChara)?.TargetObject?.GameObjectId,
        RetargetMode.LowestHpEnemy  => LowestHpEnemy()?.GameObjectId,
        _ => null,
    };

    private static IBattleChara? LowestHpEnemy() {
        IBattleChara? best = null; float bestPct = float.MaxValue;
        foreach (var o in Plugin.ObjectTable) {
            if (o is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5 /* Enemy */) continue;
            if (npc.MaxHp == 0 || npc.CurrentHp == 0) continue;
            var pct = npc.CurrentHp * 100f / npc.MaxHp;
            if (pct < bestPct) { bestPct = pct; best = npc; }
        }
        return best;
    }
}
