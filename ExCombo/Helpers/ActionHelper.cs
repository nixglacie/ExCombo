using System.Collections.Generic;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ExCombo.Helpers;

internal static class ActionHelper {
    private static readonly Dictionary<uint, bool> _ogcdCache = new();

    // oGCD = Ability (ActionCategory 4). GCDs are Spell (2) / Weaponskill (3).
    // CooldownGroup is NOT a reliable discriminator, so categorise off ActionCategory.
    public static bool IsOgcd(uint actionId) {
        if (actionId == 0) return false;
        if (_ogcdCache.TryGetValue(actionId, out var v)) return v;
        var row = Plugin.DataManager.GetExcelSheet<LuminaAction>()?.GetRow(actionId);
        v = row.HasValue && row.Value.ActionCategory.RowId == 4;
        _ogcdCache[actionId] = v;
        return v;
    }
}
