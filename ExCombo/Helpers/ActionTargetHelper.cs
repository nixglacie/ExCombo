using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ExCombo.Helpers;

// Validity checks for a candidate retarget target: can the action be used on it, and is it in
// range / line of sight. Used when walking a retarget priority chain so invalid or out-of-range
// candidates are skipped in favour of the next entry.
internal static unsafe class ActionTargetHelper {
    public static bool IsUsableOn(uint actionId, IGameObject? tgt) {
        var mgr = ActionManager.Instance();
        if (mgr == null || tgt == null || tgt.Address == 0) return false;

        var adj    = mgr->GetAdjustedActionId(actionId);   // re-enters GetAdjustedActionId hook; guarded by _inDetour
        var tgtObj = (CSGameObject*)tgt.Address;

        // Target-type / friendly-hostile / resource validity for this specific target.
        if (mgr->GetActionStatus(ActionType.Action, adj, tgt.GameObjectId) != 0) return false;

        // In range + line of sight (0 = ok).
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || player.Address == 0) return false;
        var src = (CSGameObject*)player.Address;
        return ActionManager.GetActionInRangeOrLoS(adj, src, tgtObj) == 0;
    }
}
