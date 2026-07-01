using System.Collections.Generic;
using ExCombo.Flow;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace ExCombo.Helpers;

// Built-in target resolvers for Action-node retargeting (Phase 3, from Wrath ActionRetargeting).
// A node carries an ordered priority chain of RetargetModes; ResolvePriority walks it and returns
// the GameObjectId of the first candidate that exists and can actually receive the action
// (valid target type + in range/LoS), or null to leave the cast target unchanged.
internal static class RetargetResolver {
    // Effective chain: the node's priority list, or the legacy single mode, or empty.
    private static IEnumerable<int> EffectivePriority(FlowNode node) {
        if (node.RetargetPriority.Count > 0) return node.RetargetPriority;
        if (node.RetargetMode != 0) return new[] { node.RetargetMode };
        return System.Array.Empty<int>();
    }

    public static ulong? ResolvePriority(FlowNode node, uint actionId) {
        foreach (var m in EffectivePriority(node)) {
            var obj = ResolveObject((RetargetMode)m);
            if (obj != null && ActionTargetHelper.IsUsableOn(actionId, obj))
                return obj.GameObjectId;
        }
        return null;
    }

    // Resolve a single mode to a game object (no validity filtering).
    private static IGameObject? ResolveObject(RetargetMode mode) => mode switch {
        RetargetMode.Self            => Plugin.ObjectTable.LocalPlayer,
        RetargetMode.HardTarget      => Plugin.TargetManager.Target,
        RetargetMode.FocusTarget     => Plugin.TargetManager.FocusTarget,
        RetargetMode.SoftTarget      => Plugin.TargetManager.SoftTarget,
        RetargetMode.MouseOver       => Plugin.TargetManager.MouseOverTarget,
        RetargetMode.UiMouseOver     => UiMouseOver(),
        RetargetMode.TargetOfTarget  => (Plugin.TargetManager.Target as IBattleChara)?.TargetObject,
        RetargetMode.LowestHpAlly    => PartyHelper.LowestHp(),
        RetargetMode.LowestHpAllyAbs => PartyHelper.LowestHpAbs(),
        RetargetMode.DeadMember      => PartyHelper.FirstDead(),
        RetargetMode.LowestHpEnemy   => LowestHpEnemy(),
        RetargetMode.HighestHpEnemy  => HighestHpEnemy(),
        RetargetMode.Tank            => PartyHelper.FirstByRole(1),
        RetargetMode.Melee           => PartyHelper.FirstByRole(2),
        RetargetMode.Ranged          => PartyHelper.FirstByRole(3),
        RetargetMode.Healer          => PartyHelper.FirstByRole(4),
        RetargetMode.PartySlot1       => PartyHelper.Slot(0),
        RetargetMode.PartySlot2       => PartyHelper.Slot(1),
        RetargetMode.PartySlot3       => PartyHelper.Slot(2),
        RetargetMode.PartySlot4       => PartyHelper.Slot(3),
        RetargetMode.PartySlot5       => PartyHelper.Slot(4),
        RetargetMode.PartySlot6       => PartyHelper.Slot(5),
        RetargetMode.PartySlot7       => PartyHelper.Slot(6),
        RetargetMode.PartySlot8       => PartyHelper.Slot(7),
        _ => null,
    };

    private static unsafe IGameObject? UiMouseOver() {
        var ui = Framework.Instance()->GetUIModule();
        if (ui == null) return null;
        var pronoun = ui->GetPronounModule();
        if (pronoun == null) return null;
        var obj = pronoun->UiMouseOverTarget;
        return obj == null ? null : Plugin.ObjectTable.CreateObjectReference((nint)obj);
    }

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

    private static IBattleChara? HighestHpEnemy() {
        IBattleChara? best = null; float bestPct = float.MinValue;
        foreach (var o in Plugin.ObjectTable) {
            if (o is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5 /* Enemy */) continue;
            if (npc.MaxHp == 0 || npc.CurrentHp == 0) continue;
            var pct = npc.CurrentHp * 100f / npc.MaxHp;
            if (pct > bestPct) { bestPct = pct; best = npc; }
        }
        return best;
    }
}
