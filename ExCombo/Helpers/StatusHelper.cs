using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;

namespace ExCombo.Helpers;

// Buff/debuff queries (ported & trimmed from Wrath Functions/Status.cs).
internal static class StatusHelper {
    private static IBattleChara? Scope(bool targetIsCurrent) =>
        targetIsCurrent
            ? Plugin.TargetManager.Target as IBattleChara
            : Plugin.ObjectTable.LocalPlayer;

    // Returns the matching status (optionally only those applied by the local player), or null.
    private static IStatus? Find(uint statusId, IBattleChara? on, bool sourcePlayer) {
        if (on == null) return null;
        var selfId = Plugin.ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        foreach (var s in on.StatusList) {
            if (s.StatusId != statusId) continue;
            if (sourcePlayer && s.SourceId != selfId) continue;
            return s;
        }
        return null;
    }

    public static bool Present(uint statusId, bool targetIsCurrent, bool sourcePlayer = true)
        => Find(statusId, Scope(targetIsCurrent), sourcePlayer) != null;

    // Seconds remaining; 0 when absent (so "< n" reads naturally for refresh logic).
    public static float Remaining(uint statusId, bool targetIsCurrent, bool sourcePlayer = true) {
        var s = Find(statusId, Scope(targetIsCurrent), sourcePlayer);
        return s == null ? 0f : System.MathF.Abs(s.RemainingTime);
    }

    public static int Stacks(uint statusId, bool targetIsCurrent, bool sourcePlayer = true) {
        var s = Find(statusId, Scope(targetIsCurrent), sourcePlayer);
        return s?.Param ?? 0;
    }
}
