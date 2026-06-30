using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo.Helpers;

internal static unsafe class ComboHelper {
    private static readonly Dictionary<uint, uint> _parentCache = new();

    // Live game combo state.
    public static float ComboTimer  => ActionManager.Instance()->Combo.Timer;   // seconds remaining, 0 = none
    public static uint  ComboAction => ActionManager.Instance()->Combo.Action;   // last action that set the combo

    // Required predecessor for an action; 0 = combo starter or non-combo. Cached.
    public static uint GetComboParent(uint actionId) {
        if (_parentCache.TryGetValue(actionId, out var p)) return p;
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRow(actionId);
        p = row?.ActionCombo.RowId ?? 0;
        _parentCache[actionId] = p;
        return p;
    }
}
