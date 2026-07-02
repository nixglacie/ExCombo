using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using ContentFinderCondition = Lumina.Excel.Sheets.ContentFinderCondition;

namespace ExCombo.Helpers;

// Shared duty-scope editor used by the New Flow dialog and the editor's Flow settings popup.
// Layout: a bordered box listing the current duties (Alt-gated trash each), then a single add row
// with an "Add duty" search dropdown (contained in a popup) and a "Current duty" quick-add.
// Returns true when the scope list was mutated this frame.
internal static class DutyScopeUi {
    private static string  _search = "";
    private static bool    _focus;
    private static string  _openId = "";   // which caller's popup is open (avoids id collisions)
    private static Vector2 _addAnchor;     // pin the add-duty popup directly under its button

    public static bool Draw(string id, List<uint> scope) {
        bool changed = false;
        ImGui.PushID(id);
        var popup = $"##dsadd_{id}";

        // ── Current entries (rendered inline — no scroll region) ───────────
        if (scope.Count == 0) {
            ImGui.TextDisabled("Any duty — runs everywhere (fallback)");
        } else {
            bool alt = ImGui.GetIO().KeyAlt;
            uint? remove = null;
            foreach (var d in scope) {
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.173f, 0.180f, 0.200f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.522f, 0.569f, 0.18f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1f, 0.522f, 0.569f, 0.35f));
                ImGui.PushStyleColor(ImGuiCol.Text,          alt ? new Vector4(1f, 0.522f, 0.569f, 0.80f)
                                                                 : new Vector4(0.45f, 0.46f, 0.48f, 1f));
                if (!alt) ImGui.BeginDisabled();
                if (ImGuiComponents.IconButton($"##dsrm{d}", FontAwesomeIcon.Trash)) remove = d;
                if (!alt) ImGui.EndDisabled();
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(alt ? "Remove" : "Hold Alt to remove");
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                var nm = ContentHelper.DutyName(d);
                ImGui.TextUnformatted(nm.Length > 0 ? nm : $"#{d}");
            }
            if (remove is { } r) { scope.Remove(r); changed = true; }
        }

        // ── Add row ────────────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Button,        Windows.Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Windows.Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Windows.Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        bool addClicked = ImGui.Button("+ Add duty");
        ImGui.PopStyleColor(4);
        if (addClicked) { ImGui.OpenPopup(popup); _search = ""; _focus = true; _openId = id; }
        _addAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + 2f);   // beneath the button

        var cur = ContentHelper.CurrentDutyId();
        bool canCur = cur != 0 && !scope.Contains(cur);
        ImGui.SameLine();
        if (!canCur) ImGui.BeginDisabled();
        if (ImGui.Button("+ Current duty")) { scope.Add(cur); changed = true; }
        if (!canCur) ImGui.EndDisabled();
        if (cur != 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(scope.Contains(cur) ? $"Already scoped: {ContentHelper.DutyName(cur)}"
                                                 : $"Add: {ContentHelper.DutyName(cur)}");

        // ── Add-duty search dropdown (pinned beneath the button) ───────────
        ImGui.SetNextWindowPos(_addAnchor);
        if (ImGui.BeginPopup(popup, ImGuiWindowFlags.NoMove)) {
            ImGui.SetNextItemWidth(300f);
            if (_focus && _openId == id) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            ImGui.InputTextWithHint("##dss", "search duty…", ref _search, 64);

            ImGui.BeginChild("##dsl", new Vector2(300f, 260f), true);
            var q = _search.Trim();
            if (q.Length == 0) {
                ImGui.TextDisabled("Type to search duties…");
            } else {
                var sheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
                if (sheet != null) {
                    int shown = 0;
                    foreach (var row in sheet) {
                        var nm = row.Name.ToString();
                        if (nm.Length == 0) continue;
                        if (!nm.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                        bool already = scope.Contains(row.RowId);
                        if (already) ImGui.BeginDisabled();
                        // Selecting adds but keeps the popup open for multi-add.
                        if (ImGui.Selectable($"{nm}##dsp{row.RowId}") && !already) { scope.Add(row.RowId); changed = true; }
                        if (already) ImGui.EndDisabled();
                        if (++shown >= 200) { ImGui.TextDisabled("…refine search"); break; }
                    }
                    if (shown == 0) ImGui.TextDisabled("No match.");
                }
            }
            ImGui.EndChild();
            ImGui.EndPopup();
        }

        ImGui.PopID();
        return changed;
    }
}
