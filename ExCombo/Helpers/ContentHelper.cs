using FFXIVClientStructs.FFXIV.Client.Game;
using ContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;

namespace ExCombo.Helpers;

// Duty / instance identity for flow-level scoping. The game exposes the current instance as a
// ContentFinderCondition RowId; a flow's DutyScope is a set of those ids.
internal static class ContentHelper {
    // RowId of the ContentFinderCondition for the instance the player is bound to; 0 when not in one
    // (same source as PlayerStateHelper.InDuty).
    public static unsafe uint CurrentDutyId() {
        var gm = GameMain.Instance();
        return gm != null ? gm->CurrentContentFinderConditionId : 0u;
    }

    // Human-readable duty name for editor/list labels; "" when the id is unknown/unnamed.
    public static string DutyName(uint cfcId) {
        if (cfcId == 0) return "";
        var row = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>()?.GetRowOrDefault(cfcId);
        return row?.Name.ToString() ?? "";
    }
}
