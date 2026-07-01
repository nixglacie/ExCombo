using FFXIVClientStructs.FFXIV.Client.Game;

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
}
