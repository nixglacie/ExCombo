using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using ExCombo.Flow;

namespace ExCombo.Windows;

// Shared retarget-chain editing UI, used by both the node editor's Retarget tab and the Settings
// Presets tab.
internal static class RetargetUi {
    private static readonly Vector4 RedCol = new(1f, 0.522f, 0.569f, 0.85f);
    private static readonly Vector4 DimCol = new(0.45f, 0.46f, 0.48f, 1f);
    // Selectable retarget modes (excludes None), in menu order.
    public static readonly int[] Choices = {
        (int)RetargetMode.Self, (int)RetargetMode.HardTarget, (int)RetargetMode.FocusTarget,
        (int)RetargetMode.SoftTarget, (int)RetargetMode.MouseOver, (int)RetargetMode.UiMouseOver,
        (int)RetargetMode.TargetOfTarget, (int)RetargetMode.LowestHpAlly, (int)RetargetMode.LowestHpAllyAbs,
        (int)RetargetMode.DeadMember, (int)RetargetMode.LowestHpEnemy, (int)RetargetMode.HighestHpEnemy,
        (int)RetargetMode.Tank, (int)RetargetMode.Healer, (int)RetargetMode.Melee, (int)RetargetMode.Ranged,
        (int)RetargetMode.PartySlot1, (int)RetargetMode.PartySlot2, (int)RetargetMode.PartySlot3,
        (int)RetargetMode.PartySlot4, (int)RetargetMode.PartySlot5, (int)RetargetMode.PartySlot6,
        (int)RetargetMode.PartySlot7, (int)RetargetMode.PartySlot8,
    };

    public static string Label(int mode) => (RetargetMode)mode switch {
        RetargetMode.Self            => "Self",
        RetargetMode.HardTarget      => "Current target",
        RetargetMode.FocusTarget     => "Focus target",
        RetargetMode.SoftTarget      => "Soft target",
        RetargetMode.MouseOver       => "Mouseover (world)",
        RetargetMode.UiMouseOver     => "Mouseover (party list)",
        RetargetMode.TargetOfTarget  => "Target of target",
        RetargetMode.LowestHpAlly    => "Lowest HP% ally",
        RetargetMode.LowestHpAllyAbs => "Lowest HP ally (abs)",
        RetargetMode.DeadMember      => "Dead member",
        RetargetMode.LowestHpEnemy   => "Lowest HP% enemy",
        RetargetMode.HighestHpEnemy  => "Highest HP% enemy",
        RetargetMode.Tank            => "Tank",
        RetargetMode.Healer          => "Healer",
        RetargetMode.Melee           => "Melee DPS",
        RetargetMode.Ranged          => "Ranged DPS",
        RetargetMode.PartySlot1      => "Party slot 1",
        RetargetMode.PartySlot2      => "Party slot 2",
        RetargetMode.PartySlot3      => "Party slot 3",
        RetargetMode.PartySlot4      => "Party slot 4",
        RetargetMode.PartySlot5      => "Party slot 5",
        RetargetMode.PartySlot6      => "Party slot 6",
        RetargetMode.PartySlot7      => "Party slot 7",
        RetargetMode.PartySlot8      => "Party slot 8",
        _ => "None",
    };

    // Full preset CRUD list (name + chain + reorder/delete + add). Returns true if anything changed.
    // Shared by the Settings Presets tab and the node editor's nested manager popup.
    public static bool DrawPresetManager(List<RetargetPreset> presets) {
        bool changed = false;
        int upIdx = -1, downIdx = -1, delIdx = -1;

        for (var i = 0; i < presets.Count; i++) {
            var pr = presets[i];
            ImGui.PushID(i);
            // Match name-input right edge to the indented chain combos (indent + 200f)
            // so the preset arrows share a column with the chain-row arrows below.
            ImGui.SetNextItemWidth(ImGui.GetStyle().IndentSpacing + 200f);
            var nm = pr.Name;
            if (ImGui.InputTextWithHint("##pname", "preset name", ref nm, 48)) { pr.Name = nm; changed = true; }
            ImGui.SameLine();
            bool alt = ImGui.GetIO().KeyAlt;
            bool trashHover;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push()) {
                ImGui.PushStyleColor(ImGuiCol.Text, Style.Accent(0.85f));
                if (ImGui.Button(FontAwesomeIcon.ArrowUp.ToIconString()))   upIdx   = i;
                ImGui.SameLine();
                if (ImGui.Button(FontAwesomeIcon.ArrowDown.ToIconString())) downIdx = i;
                ImGui.PopStyleColor();
                ImGui.SameLine();
                // Delete requires Alt held, like flow deletion.
                ImGui.PushStyleColor(ImGuiCol.Text, alt ? RedCol : DimCol);
                if (!alt) ImGui.BeginDisabled();
                if (ImGui.Button(FontAwesomeIcon.TrashAlt.ToIconString())) delIdx = i;
                if (!alt) ImGui.EndDisabled();
                ImGui.PopStyleColor();
                trashHover = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            }
            if (trashHover) ImGui.SetTooltip(alt ? "Delete preset" : "Hold Alt to delete");
            ImGui.Indent();
            if (DrawChainEditor($"p{i}", pr.Modes)) changed = true;
            ImGui.Unindent();
            ImGui.Separator();
            ImGui.PopID();
        }

        if (upIdx > 0)                              { (presets[upIdx - 1], presets[upIdx]) = (presets[upIdx], presets[upIdx - 1]); changed = true; }
        if (downIdx >= 0 && downIdx < presets.Count - 1) { (presets[downIdx + 1], presets[downIdx]) = (presets[downIdx], presets[downIdx + 1]); changed = true; }
        if (delIdx >= 0)                            { presets.RemoveAt(delIdx); changed = true; }

        if (presets.Count == 0) ImGui.TextDisabled("No presets yet.");

        ImGui.Spacing();
        if (ImGui.Button("+ New preset")) { presets.Add(new RetargetPreset { Name = "New preset" }); changed = true; }
        return changed;
    }

    // Draw the ordered mode rows (dropdown + up/down/trash) plus an "Add target" row.
    // Returns true if the list was mutated this frame.
    public static bool DrawChainEditor(string id, List<int> list) {
        bool changed = false;
        int moveUp = -1, moveDown = -1, remove = -1;
        ImGui.PushID(id);
        for (var i = 0; i < list.Count; i++) {
            ImGui.PushID(i);
            var curIdx = Array.IndexOf(Choices, list[i]);
            if (curIdx < 0) curIdx = 0;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.BeginCombo("##mode", Label(list[i]))) {
                for (var c = 0; c < Choices.Length; c++)
                    if (ImGui.Selectable(Label(Choices[c]), c == curIdx) && list[i] != Choices[c]) {
                        list[i] = Choices[c]; changed = true;
                    }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            bool alt = ImGui.GetIO().KeyAlt;
            bool trashHover;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push()) {
                ImGui.PushStyleColor(ImGuiCol.Text, Style.Accent(0.85f));
                if (ImGui.Button(FontAwesomeIcon.ArrowUp.ToIconString()))   moveUp   = i;
                ImGui.SameLine();
                if (ImGui.Button(FontAwesomeIcon.ArrowDown.ToIconString())) moveDown = i;
                ImGui.PopStyleColor();
                ImGui.SameLine();
                // Delete requires Alt held, like preset/flow deletion.
                ImGui.PushStyleColor(ImGuiCol.Text, alt ? RedCol : DimCol);
                if (!alt) ImGui.BeginDisabled();
                if (ImGui.Button(FontAwesomeIcon.TrashAlt.ToIconString()))  remove   = i;
                if (!alt) ImGui.EndDisabled();
                ImGui.PopStyleColor();
                trashHover = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            }
            if (trashHover) ImGui.SetTooltip(alt ? "Delete" : "Hold Alt to delete");
            ImGui.PopID();
        }

        if (moveUp > 0)                            { (list[moveUp - 1], list[moveUp]) = (list[moveUp], list[moveUp - 1]); changed = true; }
        if (moveDown >= 0 && moveDown < list.Count - 1) { (list[moveDown + 1], list[moveDown]) = (list[moveDown], list[moveDown + 1]); changed = true; }
        if (remove >= 0)                           { list.RemoveAt(remove); changed = true; }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Style.Accent(0.85f));
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            if (ImGui.Button(FontAwesomeIcon.Plus.ToIconString() + "##add")) { list.Add(Choices[0]); changed = true; }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("Add target");
        ImGui.PopID();
        return changed;
    }
}
