using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ExCombo.Helpers;

// Cooldown / charge / readiness queries (from Wrath Functions/Cooldown.cs & Action.cs).
internal static unsafe class CooldownHelper {
    private static uint Adjusted(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr != null ? mgr->GetAdjustedActionId(actionId) : actionId;
    }

    // Seconds until the action (or its next charge) is usable again; 0 when ready.
    public static float Remaining(uint actionId) {
        var mgr = ActionManager.Instance();
        if (mgr == null) return 0f;
        var adj       = Adjusted(actionId);
        var remaining = mgr->GetRecastTime(ActionType.Action, adj)
                      - mgr->GetRecastTimeElapsed(ActionType.Action, adj);
        return remaining > 0f ? remaining : 0f;
    }

    public static int Charges(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr == null ? 0 : (int)mgr->GetCurrentCharges(Adjusted(actionId));
    }

    public static int MaxCharges(uint actionId)
        => (int)ActionManager.GetMaxCharges(Adjusted(actionId), 0);

    public static bool Ready(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr != null && mgr->GetActionStatus(ActionType.Action, Adjusted(actionId)) == 0;
    }

    // Seconds since the action last went on cooldown (game-native; caps at the recast total). For a
    // one-off "used within N sec" test this is enough; ActionTracker.TimeSinceUsed is unbounded.
    public static float Elapsed(uint actionId) {
        var mgr = ActionManager.Instance();
        return mgr == null ? 0f : mgr->GetRecastTimeElapsed(ActionType.Action, Adjusted(actionId));
    }

    // Player is high enough level for the action (from Wrath Functions/Action.cs LevelChecked). The
    // catalog picker already surfaces ClassJobLevel, so low-level/level-synced flows can gate on it.
    // Note: does not verify quest unlocks — level covers the leveling/sync case.
    public static bool LevelChecked(uint actionId) {
        var row = Plugin.DataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        if (row is null) return false;
        var lvl = Plugin.ObjectTable.LocalPlayer?.Level ?? 0;
        return lvl >= row.Value.ClassJobLevel;
    }
}
