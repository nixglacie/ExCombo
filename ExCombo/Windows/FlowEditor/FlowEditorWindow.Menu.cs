using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ExCombo.Flow;
using ExCombo.Helpers;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ExCombo.Windows;

public partial class FlowEditorWindow {
    private static bool IconMenuItem(FontAwesomeIcon icon, string label, uint? iconColor = null) {
        var dl = ImGui.GetWindowDrawList();
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        var iconStr = icon.ToIconString();
        var iconW   = ImGui.CalcTextSize(iconStr).X;
        ImGui.PopFont();
        var spaceW  = MathF.Max(ImGui.CalcTextSize(" ").X, 1f);
        var pad     = (int)MathF.Ceiling((iconW + 10f) / spaceW);
        var result  = ImGui.MenuItem(new string(' ', pad) + label);
        if (ImGui.IsItemVisible()) {
            var rMin = ImGui.GetItemRectMin();
            var rMax = ImGui.GetItemRectMax();
            var sz   = ImGui.GetFontSize();
            var col  = iconColor ?? ImGui.GetColorU32(ImGuiCol.Text);
            var ipos = new Vector2(rMin.X + 4f, rMin.Y + (rMax.Y - rMin.Y - sz) * 0.5f);
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            dl.AddText(ipos, col, iconStr);
            ImGui.PopFont();
        }
        return result;
    }

    private static readonly Dictionary<string, Vector2> _iconMenuOffset = new();

    // Same icon-overlay trick as IconMenuItem, but for a submenu header (ImGui.BeginMenu).
    // When the submenu opens, the last-item rect no longer references the header, so its rect is
    // only reliable on closed frames. Capture the cursor up front, derive the exact icon offset
    // from the rect while closed and cache it, then reuse that offset while open — pixel-stable.
    private static bool IconBeginMenu(FontAwesomeIcon icon, string label, uint? iconColor = null) {
        var dl     = ImGui.GetWindowDrawList();
        var rowPos = ImGui.GetCursorScreenPos();
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        var iconStr = icon.ToIconString();
        var iconW   = ImGui.CalcTextSize(iconStr).X;
        ImGui.PopFont();
        var spaceW  = MathF.Max(ImGui.CalcTextSize(" ").X, 1f);
        var pad     = (int)MathF.Ceiling((iconW + 10f) / spaceW);
        var sz      = ImGui.GetFontSize();

        var open    = ImGui.BeginMenu(new string(' ', pad) + label);

        Vector2 off;
        if (!open) {
            var rMin = ImGui.GetItemRectMin();
            var rMax = ImGui.GetItemRectMax();
            off = new Vector2(rMin.X + 4f - rowPos.X, rMin.Y + (rMax.Y - rMin.Y - sz) * 0.5f - rowPos.Y);
            _iconMenuOffset[label] = off;
        } else {
            off = _iconMenuOffset.TryGetValue(label, out var c)
                ? c : new Vector2(4f, ImGui.GetStyle().FramePadding.Y);
        }

        var col = iconColor ?? ImGui.GetColorU32(ImGuiCol.Text);
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        dl.AddText(rowPos + off, col, iconStr);
        ImGui.PopFont();
        return open;
    }
}
