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
    private void OpenNoteEdit(string nodeId) {
        _noteEditNodeId      = nodeId;
        _noteEditText        = _flow!.Nodes.Find(n => n.Id == nodeId)?.NoteText ?? "";
        _pendingOpenNoteEdit = true;
    }

    private void DrawNoteEdit() {
        if (_noteEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Edit Note##noteedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Note text");
        ImGui.InputTextMultiline("##notetext", ref _noteEditText, 1024, new Vector2(CondW, 140f));

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _noteEditNodeId);
            if (node != null) { node.NoteText = _noteEditText; Commit(); }
            _noteEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.SameLine();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _noteEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();

        ImGui.EndPopup();
    }

    private void OpenBranchEdit(string nodeId, int currentCount) {
        _branchEditNodeId    = nodeId;
        _branchEditCount     = currentCount;
        _pendingOpenBranchEdit = true;
    }

    private void OpenRetargetEdit(string nodeId) {
        _editNodeId = nodeId;
        var node = _flow!.Nodes.Find(n => n.Id == nodeId);
        if (node != null) SeedEditStaging(node);
        _editActiveTab       = 1;
        _pendingOpenNodeEdit = true;
    }

    // Copy the node's current action + retarget state into the staging buffers (migrating a legacy
    // single retarget mode into the chain form). Tabs edit the buffers; OK writes them back.
    private void SeedEditStaging(FlowNode node) {
        _editActionId    = node.ActionId;
        _editActionLabel = node.ActionLabel;
        _editIconId      = node.IconId;
        _editIsOgcd      = node.IsOgcd;
        _editRetarget    = node.RetargetPriority.Count > 0 ? new(node.RetargetPriority)
                         : node.RetargetMode != 0 ? new() { node.RetargetMode }
                         : new();
    }

    private void DrawBranchEdit() {
        if (_branchEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Priority Outputs##branchedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Output count");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputInt("##outcount", ref _branchEditCount);
        if (_branchEditCount < 2)  _branchEditCount = 2;
        if (_branchEditCount > 16) _branchEditCount = 16;

        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _branchEditNodeId);
            if (node != null) {
                for (var p = _branchEditCount; p < node.OutputCount; p++)
                    _flow.Edges.RemoveAll(e => e.FromNodeId == node.Id && e.FromPortIndex == p);
                node.OutputCount = _branchEditCount;
                FlowExecutor.InvalidateFlow(_flow.Id);
                Commit();
            }
            _branchEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.SameLine();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _branchEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();

        ImGui.EndPopup();
    }

    private void OpenLogicEdit(string nodeId) {
        var node = _flow!.Nodes.Find(n => n.Id == nodeId);
        if (node == null) return;
        _logicEditNodeId      = nodeId;
        _logicEditCount       = node.LogicInputCount;
        _logicEditExpr        = node.LogicExpr is "" ? "1 AND 2" : node.LogicExpr;
        _pendingOpenLogicEdit = true;
    }

    private void DrawLogicEdit() {
        if (_logicEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Edit Logic##logicedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Inputs");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("##logicincount", ref _logicEditCount);
        if (_logicEditCount < 2) _logicEditCount = 2;
        if (_logicEditCount > 8) _logicEditCount = 8;

        ImGui.Spacing();
        ImGui.TextDisabled("Expression");
        ImGui.SetNextItemWidth(320f);
        ImGui.InputText("##logicexpr", ref _logicEditExpr, 256);

        // Live validation.
        var ast = LogicExpr.Parse(_logicEditExpr, out var parseError);
        var err = ast == null                       ? parseError
                : ast.MaxInput > _logicEditCount    ? $"expression references input {ast.MaxInput}, but only {_logicEditCount} inputs exist"
                : "";
        if (err.Length > 0) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), err);
        else                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "expression is valid");

        ImGui.PushTextWrapPos(360f);
        ImGui.TextDisabled("Inputs are the numbered ports on the node's left, fed by condition (or "
                         + "other Logic) outputs — wire from a false/red port to feed the negated "
                         + "value. Operators: AND OR NOT XOR or && || ! ^, with parentheses. "
                         + "Unwired inputs count as false.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        var canSave = err.Length == 0;
        if (!canSave) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _logicEditNodeId);
            if (node != null) {
                // Drop predicate wires into slots that no longer exist.
                _flow.Edges.RemoveAll(e => e.ToNodeId == node.Id && e.ToPortIndex > _logicEditCount);
                node.LogicInputCount = _logicEditCount;
                node.LogicExpr       = _logicEditExpr.Trim();
                FlowExecutor.InvalidateFlow(_flow.Id);
                Commit();
            }
            _logicEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        if (!canSave) ImGui.EndDisabled();

        ImGui.SameLine();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _logicEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();

        ImGui.EndPopup();
    }

    private void OpenKeybindEdit(string nodeId) {
        var node = _flow!.Nodes.Find(n => n.Id == nodeId);
        if (node == null) return;
        _keybindEditNodeId      = nodeId;
        _keybindEditVk          = node.CheckParamId != 0 ? node.CheckParamId : 16;   // default Shift
        _pendingOpenKeybindEdit = true;
    }

    private void DrawKeybindEdit() {
        if (_keybindEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Edit Keybind##keybindedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Key");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##keybindkey", KeyName(_keybindEditVk))) {
            foreach (var vk in new uint[] { 16, 17, 18 }) {
                if (ImGui.Selectable(KeyName(vk), _keybindEditVk == vk)) _keybindEditVk = vk;
            }
            ImGui.EndCombo();
        }

        ImGui.PushTextWrapPos(320f);
        ImGui.TextDisabled("The gate is true while the key is held (game window must be focused).");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _keybindEditNodeId);
            if (node != null) {
                node.CheckParamId = _keybindEditVk;
                FlowExecutor.InvalidateFlow(_flow.Id);
                Commit();
            }
            _keybindEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.SameLine();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _keybindEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();

        ImGui.EndPopup();
    }

    private void OpenToggleEdit(string nodeId) {
        var node = _flow!.Nodes.Find(n => n.Id == nodeId);
        if (node == null) return;
        _toggleEditNodeId      = nodeId;
        _toggleEditName        = node.ActionLabel;
        _toggleEditOn          = node.ToggleOn;
        _toggleEditCopied      = false;
        _pendingOpenToggleEdit = true;
    }

    private string ToggleCommand(string name) => $"/excombo toggle {name.Trim()}";

    private void DrawToggleEdit() {
        if (_toggleEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Edit Toggle##toggleedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.TextDisabled("Name");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##togglename", ref _toggleEditName, 64);
        ImGui.Checkbox("On", ref _toggleEditOn);

        // One-click macro command for a hotbar macro.
        ImGui.Spacing();
        var cmd = ToggleCommand(_toggleEditName is "" ? "<name>" : _toggleEditName);
        ImGui.TextDisabled(cmd);
        var canCopy = _toggleEditName.Trim().Length > 0;
        if (!canCopy) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy, new Vector2(26f, 24f))) {
            _toggleEditCopied = Helpers.ClipboardHelper.SetText(ToggleCommand(_toggleEditName));
        }
        if (!canCopy) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy command — paste it into a game macro");
        if (_toggleEditCopied) {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "copied!");
        }

        ImGui.PushTextWrapPos(320f);
        ImGui.TextDisabled("Flip it here, via right-click → Switch On/Off, or with the command "
                         + "above (put it in a macro for a hotbar button). The state persists "
                         + "across reloads.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _toggleEditNodeId);
            if (node != null) {
                node.ActionLabel = _toggleEditName.Trim();
                node.ToggleOn    = _toggleEditOn;
                FlowExecutor.InvalidateFlow(_flow.Id);
                Commit();
            }
            _toggleEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.SameLine();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _toggleEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();

        ImGui.EndPopup();
    }

    private void OpenPicker(string nodeId) {
        _editNodeId       = nodeId;
        _pickerSearch     = "";
        _pickerLastSearch = "\0";
        _pickerResults.Clear();
        BuildJobCategorySet();
        var node = _flow!.Nodes.Find(n => n.Id == nodeId);
        if (node != null) SeedEditStaging(node);
        _editActiveTab       = 0;
        _pendingOpenNodeEdit = true;
    }

    // Merged Action/Retarget editor: one modal, two pill tabs, both editing the node live.
    private void DrawNodeEdit() {
        if (_editNodeId == null) return;
        var node = _flow!.Nodes.Find(n => n.Id == _editNodeId);
        if (node == null) { _editNodeId = null; return; }

        // Auto-fit to content (non-resizable), like the condition popup.
        if (!ImGui.BeginPopupModal("Edit Action##nodeedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        // Retarget only applies to Action chain nodes (not triggers).
        var canRetarget = node.Type == NodeType.Action;
        if (!canRetarget) _editActiveTab = 0;
        DrawTabButton(FontAwesomeIcon.Bolt, "Action", _editActiveTab == 0, () => _editActiveTab = 0);
        if (canRetarget) {
            ImGui.SameLine(0, 6f);
            DrawTabButton(FontAwesomeIcon.Crosshairs, "Retarget", _editActiveTab == 1, () => _editActiveTab = 1);
        }
        ImGui.Separator();

        if (_editActiveTab == 0) DrawActionTab();
        else                     DrawRetargetTab();
        ImGui.Dummy(new Vector2(0f, 4f));

        // OK / Cancel — apply staged edits on OK, discard on Cancel (matches the condition popup).
        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            node.ActionId    = _editActionId;
            node.ActionLabel = _editActionLabel;
            node.IconId      = _editIconId;
            node.IsOgcd      = _editIsOgcd;
            if (canRetarget) {
                node.RetargetPriority = new(_editRetarget);
                node.RetargetMode     = 0;   // fully migrated to the chain
            }
            FlowExecutor.InvalidateFlow(_flow.Id);
            Commit();
            _editNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        ImGui.SameLine();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) { _editNodeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.PopStyleVar();
        ImGui.EndPopup();
    }

    private const float ActionTabWidth  = 400f;
    private const float ActionListHeight = 340f;

    private void DrawActionTab() {
        ImGui.SetNextItemWidth(ActionTabWidth);
        ImGui.InputTextWithHint("##search", "search action…", ref _pickerSearch, 256);

        if (_pickerSearch != _pickerLastSearch) {
            _pickerLastSearch = _pickerSearch;
            UpdatePickerResults();
        }

        DrawTabButton("PvE", !_pickerPvpTab, () => _pickerPvpTab = false);
        ImGui.SameLine(0, 6f);
        DrawTabButton("PvP",  _pickerPvpTab, () => _pickerPvpTab = true);

        var pvp = _pickerPvpTab;
        ImGui.BeginChild(pvp ? "##rpvp" : "##rpve", new Vector2(ActionTabWidth, ActionListHeight), true);
        foreach (var (id, name, icon, level, isPvp) in _pickerResults) {
            if (isPvp != pvp) continue;
            var rowStart = ImGui.GetCursorPos();
            bool clicked = ImGui.Selectable($"##s{id}", _editActionId == id, ImGuiSelectableFlags.None, new Vector2(0f, 36f));
            ImGui.SetCursorPos(rowStart);
            if (icon != 0) {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon))?.GetWrapOrDefault();
                if (tex != null) {
                    ImGui.Image(tex.Handle, new Vector2(36f, 36f));
                    ImGui.SameLine();
                }
            }
            ImGui.SetCursorPosY(rowStart.Y + (36f - ImGui.GetTextLineHeight()) * 0.5f);
            ImGui.TextUnformatted($"Lv{level}  {name}");
            if (clicked) {
                _editActionId    = id;
                _editActionLabel = name;
                _editIconId      = icon;
                _editIsOgcd      = ActionHelper.IsOgcd(id);   // staged; applied on OK
            }
        }
        ImGui.EndChild();
    }

    private void DrawRetargetTab() {
        ImGui.TextDisabled("Tried top-to-bottom; first valid, in-range target wins.");
        ImGui.Spacing();

        // ── Preset bar: load a saved chain, or jump to the presets manager ───
        var presets = Plugin.Config.RetargetPresets;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##loadpreset", "Load preset")) {
            if (presets.Count == 0) ImGui.TextDisabled("(no presets)");
            for (var p = 0; p < presets.Count; p++) {
                var pr = presets[p];
                if (ImGui.Selectable((pr.Name.Length > 0 ? pr.Name : "(unnamed)") + $"##lp{p}"))
                    _editRetarget = new(pr.Modes);   // snapshot copy; applied to node on OK
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cog, "Manage presets"))
            ImGui.OpenPopup("Manage presets##presetmgr");
        var mgrBtnMin = ImGui.GetItemRectMin();
        var mgrBtnMax = ImGui.GetItemRectMax();

        // Nested popup over the node modal — anchored under the button, not at the cursor.
        ImGui.SetNextWindowPos(new Vector2(mgrBtnMin.X, mgrBtnMax.Y + 4f));
        ImGui.SetNextWindowSize(new Vector2(380f, 420f), ImGuiCond.Appearing);
        if (ImGui.BeginPopup("Manage presets##presetmgr")) {
            if (RetargetUi.DrawPresetManager(Plugin.Config.RetargetPresets)) Plugin.Config.Save();
            ImGui.EndPopup();
        }

        ImGui.Separator();
        ImGui.Spacing();

        RetargetUi.DrawChainEditor("rtg", _editRetarget);
    }

    private static void DrawTabButton(string label, bool active, Action onClick) {
        var accent    = Style.AccentColor;
        var accentHov = Style.AccentHover;
        var accentAct = Style.AccentActive;
        var bg3       = new Vector4(0.173f, 0.180f, 0.200f, 1f);
        var bg3Hov    = new Vector4(0.216f, 0.224f, 0.247f, 1f);
        var textDark  = new Vector4(0.102f, 0.106f, 0.118f, 1f);
        var textDim   = new Vector4(0.565f, 0.573f, 0.588f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button,        active ? accent    : bg3);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? accentHov : bg3Hov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  active ? accentAct : bg3Hov);
        ImGui.PushStyleColor(ImGuiCol.Text,          active ? textDark  : textDim);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(20f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 20f);
        if (ImGui.Button(label)) onClick();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
    }

    // Pill tab with a leading FontAwesome icon. Renders icon (icon font) + label (default font)
    // over one sized button so the pill styling is preserved.
    private static void DrawTabButton(FontAwesomeIcon icon, string label, bool active, Action onClick) {
        var bg3      = new Vector4(0.173f, 0.180f, 0.200f, 1f);
        var bg3Hov   = new Vector4(0.216f, 0.224f, 0.247f, 1f);
        var textDark = new Vector4(0.102f, 0.106f, 0.118f, 1f);
        var textDim  = new Vector4(0.565f, 0.573f, 0.588f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button,        active ? Style.AccentColor  : bg3);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? Style.AccentHover  : bg3Hov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  active ? Style.AccentActive : bg3Hov);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(20f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 20f);

        var ico = icon.ToIconString();
        Vector2 icoSz;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push()) icoSz = ImGui.CalcTextSize(ico);
        var txtSz = ImGui.CalcTextSize(label);
        const float gap = 6f;
        var pad     = ImGui.GetStyle().FramePadding;
        var btnSize = new Vector2(icoSz.X + gap + txtSz.X + pad.X * 2f, MathF.Max(icoSz.Y, txtSz.Y) + pad.Y * 2f);

        var p       = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##tab_{label}", btnSize);
        var dl      = ImGui.GetWindowDrawList();
        var textCol = ImGui.GetColorU32(active ? textDark : textDim);
        var cy      = p.Y + btnSize.Y * 0.5f;
        var x       = p.X + pad.X;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle?.Push())
            dl.AddText(new Vector2(x, cy - icoSz.Y * 0.5f), textCol, ico);
        dl.AddText(new Vector2(x + icoSz.X + gap, cy - txtSz.Y * 0.5f), textCol, label);

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
        if (clicked) onClick();
    }

    private static readonly string[] _allJobs = {
        "GNB", "PLD", "WAR", "DRK", "WHM", "SCH", "AST", "SGE", "MNK", "DRG", "NIN",
        "SAM", "RPR", "VPR", "BRD", "MCH", "DNC", "BLM", "SMN", "RDM", "PCT"
    };

    private void BuildJobCategorySet() {
        _pickerJobCategoryIds          = null;
        _pickerJobExclusiveCategoryIds = null;
        var job = _flow?.Job;
        if (string.IsNullOrEmpty(job)) return;
        var catSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJobCategory>();
        if (catSheet == null) return;
        _pickerJobCategoryIds          = new HashSet<uint>();
        _pickerJobExclusiveCategoryIds = new HashSet<uint>();
        foreach (var cat in catSheet) {
            if (!CategoryHasJob(cat, job)) continue;
            _pickerJobCategoryIds.Add(cat.RowId);
            // Exclusive = only this job's flag set; evolved/combo actions (e.g. Continuation) live
            // here. Broad categories (roles, "all classes") are skipped so they don't leak junk.
            var count = 0;
            foreach (var j in _allJobs) { if (CategoryHasJob(cat, j)) count++; if (count > 1) break; }
            if (count == 1) _pickerJobExclusiveCategoryIds.Add(cat.RowId);
        }
    }

    private static bool CategoryHasJob(Lumina.Excel.Sheets.ClassJobCategory cat, string job) => job switch {
        "GNB" => cat.GNB, "PLD" => cat.PLD, "WAR" => cat.WAR, "DRK" => cat.DRK,
        "WHM" => cat.WHM, "SCH" => cat.SCH, "AST" => cat.AST, "SGE" => cat.SGE,
        "MNK" => cat.MNK, "DRG" => cat.DRG, "NIN" => cat.NIN, "SAM" => cat.SAM,
        "RPR" => cat.RPR, "VPR" => cat.VPR,
        "BRD" => cat.BRD, "MCH" => cat.MCH, "DNC" => cat.DNC,
        "BLM" => cat.BLM, "SMN" => cat.SMN, "RDM" => cat.RDM, "PCT" => cat.PCT,
        _ => false
    };

    private void UpdatePickerResults() {
        _pickerResults.Clear();
        var sheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
        if (sheet == null) return;

        var query = _pickerSearch.Trim();
        var jobScoped = _pickerJobCategoryIds != null;

        foreach (var row in sheet) {
            var assignable = row.IsPlayerAction || row.IsPvP;
            var catId      = row.ClassJobCategory.RowId;
            // Assignable actions: keep, scoped to the job's categories below.
            // Non-assignable actions (evolved combos like Continuation, hidden by the game from
            // hotbars but still targetable by id): only when job-scoped AND in this job's
            // exclusive single-job category, so we don't pull in unrelated unassignable junk.
            if (!assignable) {
                if (!jobScoped || !_pickerJobExclusiveCategoryIds!.Contains(catId)) continue;
            }
            var name = row.Name.ToString();
            if (name == "") continue;
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (jobScoped && assignable && !_pickerJobCategoryIds!.Contains(catId)) continue;
            _pickerResults.Add((row.RowId, name, (uint)row.Icon, row.ClassJobLevel, row.IsPvP));
            if (_pickerResults.Count >= 200) break;
        }
    }

    private static uint GetJobIconId(string job) {
        if (_jobIconCache.TryGetValue(job, out var cached)) return cached;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (sheet != null) {
            foreach (var row in sheet) {
                if (row.Abbreviation.ToString() == job) {
                    var id = 62100u + row.RowId;
                    _jobIconCache[job] = id;
                    return id;
                }
            }
        }
        _jobIconCache[job] = 0;
        return 0;
    }

    private void OpenConditionEdit(string nodeId) {
        var node             = _flow!.Nodes.Find(n => n.Id == nodeId);
        _condEditNodeId      = nodeId;
        _condEditType        = node?.Type ?? NodeType.Condition;
        _condFieldSearch     = "";
        _condEditOp          = node?.ConditionCompareOp ?? 5;
        if (_condEditType == NodeType.Condition) {
            _condFieldSelected = node?.ConditionField ?? "";
            _condEditVal       = (int)(node?.ConditionCompareVal ?? 1f);
        } else {
            _condFieldSelected   = node?.CheckField ?? "";
            _condEditValF        = node?.ConditionCompareVal ?? 1f;
            _condParamId         = node?.CheckParamId ?? 0;
            _condParamIcon       = node?.IconId ?? 0;
            _condParam2          = node?.CheckParam2 ?? 0f;
            _condTarget          = node?.CheckTarget ?? 0;
            _condSource          = node?.CheckSource ?? 0;
            _condParamSearch     = "";
            _condParamLastSearch = "\0";
            _condParamLastKind   = CheckParamKind.None;
            BuildJobCategorySet();   // job-scope the action list like the PvE picker tab
        }
        _pendingOpenCondEdit = true;
    }

    private void DrawConditionEdit() {
        if (_condEditNodeId == null) return;

        if (!ImGui.BeginPopupModal("Edit Condition##condedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)) return;

        if (_condEditType == NodeType.Condition) DrawJobConditionBody();
        else                                     DrawParamConditionBody();

        ImGui.EndPopup();
    }

    private void DrawJobConditionBody() {
        var fields = JobGaugeRegistry.GetFields(_flow?.Job ?? "");

        ImGui.TextDisabled("Field");
        ImGui.SetNextItemWidth(CondW);
        ImGui.InputTextWithHint("##cfsearch", "search…", ref _condFieldSearch, 64);

        ImGui.BeginChild("##cffield", new Vector2(CondW, 160f), true);
        if (fields != null) {
            foreach (var f in fields) {
                if (_condFieldSearch.Length > 0
                    && !f.Name.Contains(_condFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable(f.Name, f.Name == _condFieldSelected))
                    _condFieldSelected = f.Name;
            }
        } else {
            ImGui.TextDisabled("No gauge fields for this job.");
        }
        ImGui.EndChild();

        ImGui.Spacing();
        string[] opLabels = ["==", "!=", "<", "≤", ">", "≥"];
        ImGui.TextDisabled("Compare");
        ImGui.SetNextItemWidth(70f);
        ImGui.Combo("##cfop", ref _condEditOp, opLabels, opLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(CondW - 70f - ImGui.GetStyle().ItemSpacing.X);
        ImGui.DragInt("##cfval", ref _condEditVal, 1f);
        ImGui.Spacing();

        DrawCondButtons(node => {
            node.ConditionField      = _condFieldSelected;
            node.ConditionCompareOp  = _condEditOp;
            node.ConditionCompareVal = _condEditVal;
        });
    }

    private void DrawParamConditionBody() {
        var defs = _condEditType == NodeType.GaugeCondition
            ? ConditionCatalog.ForGauge(_flow?.Job ?? "")
            : ConditionCatalog.For(_condEditType);

        ImGui.TextDisabled("Check");
        ImGui.SetNextItemWidth(CondW);
        ImGui.InputTextWithHint("##cfsearch", "search…", ref _condFieldSearch, 64);

        ImGui.BeginChild("##cffield", new Vector2(CondW, 120f), true);
        if (defs != null)
            foreach (var d in defs) {
                if (_condFieldSearch.Length > 0
                    && !d.Label.Contains(_condFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable(d.Label, d.Key == _condFieldSelected))
                    _condFieldSelected = d.Key;
            }
        ImGui.EndChild();

        var def = _condEditType == NodeType.GaugeCondition
            ? ConditionCatalog.FindGauge(_flow?.Job ?? "", _condFieldSelected)
            : ConditionCatalog.Find(_condEditType, _condFieldSelected);
        ImGui.Spacing();

        if (def != null) {
            // Parameter widget (action / status picker, or a numeric value).
            if (def.Param is CheckParamKind.ActionId or CheckParamKind.StatusId) {
                var isStatus = def.Param == CheckParamKind.StatusId;
                ImGui.TextDisabled(isStatus ? "Status" : "Action");
                var cur = ResolveParamName(def.Param, _condParamId);
                ImGui.TextUnformatted(_condParamId == 0 ? "(none)" : $"{cur} ({_condParamId})");

                ImGui.SetNextItemWidth(CondW);
                ImGui.InputTextWithHint("##cparam", isStatus ? "search status…" : "search action…", ref _condParamSearch, 64);
                if (_condParamSearch != _condParamLastSearch || _condParamLastKind != def.Param) {
                    _condParamLastSearch = _condParamSearch;
                    _condParamLastKind   = def.Param;
                    BuildCondParamResults(def.Param);
                }
                ImGui.BeginChild("##cparamlist", new Vector2(CondW, 180f), true);
                foreach (var (id, name, icon, level) in _condParamResults) {
                    var rs = ImGui.GetCursorPos();
                    if (ImGui.Selectable($"##cp{id}", id == _condParamId, ImGuiSelectableFlags.None, new Vector2(0f, 36f)))
                        { _condParamId = id; _condParamLabel = name; _condParamIcon = icon; }
                    if (icon != 0) {
                        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon))?.GetWrapOrDefault();
                        if (tex != null) {
                            // Status icons aren't square — fit into a 36px box preserving aspect ratio.
                            var aspect = tex.Height > 0 ? (float)tex.Width / tex.Height : 1f;
                            var sizeXY = aspect >= 1f ? new Vector2(36f, 36f / aspect) : new Vector2(36f * aspect, 36f);
                            var off    = new Vector2((36f - sizeXY.X) * 0.5f, (36f - sizeXY.Y) * 0.5f);
                            ImGui.SetCursorPos(rs + off);
                            ImGui.Image(tex.Handle, sizeXY);
                        }
                    }
                    ImGui.SetCursorPos(new Vector2(rs.X + (icon != 0 ? 40f : 0f), rs.Y + (36f - ImGui.GetTextLineHeight()) * 0.5f));
                    ImGui.TextUnformatted(isStatus ? name : $"Lv{level}  {name}");
                    ImGui.SetCursorPos(new Vector2(rs.X, rs.Y + 40f));   // pin row height (+4px gap) so rows don't overlap
                }
                ImGui.EndChild();
            } else if (def.Param is CheckParamKind.Range or CheckParamKind.Number) {
                ImGui.TextDisabled(def.Param == CheckParamKind.Range ? "Range / threshold" : "Value");
                ImGui.SetNextItemWidth(CondW);
                int p2 = (int)MathF.Round(_condParam2);
                ImGui.DragInt("##cparam2", ref p2, 1f);
                _condParam2 = p2;
            }

            // Status/Target scope toggle.
            if (def.HasTarget) {
                ImGui.Spacing();
                ImGui.TextDisabled("On");
                ImGui.RadioButton("Player##ct", ref _condTarget, 0); ImGui.SameLine();
                ImGui.RadioButton("Target##ct", ref _condTarget, 1);
            }

            // Status source scope toggle (applied by me vs any owner).
            if (def.HasSource) {
                ImGui.Spacing();
                ImGui.TextDisabled("Source");
                ImGui.RadioButton("Me##cs", ref _condSource, 0); ImGui.SameLine();
                ImGui.RadioButton("Anyone##cs", ref _condSource, 1);
            }

            // Comparison.
            ImGui.Spacing();
            if (def.Bool) {
                ImGui.TextDisabled("State");
                int isTrue = _condEditValF >= 1f ? 0 : 1;
                string[] tf = ["is true", "is false"];
                ImGui.SetNextItemWidth(CondW);
                if (ImGui.Combo("##cbool", ref isTrue, tf, tf.Length)) { }
                _condEditOp   = (int)CompareOp.Eq;
                _condEditValF = isTrue == 0 ? 1f : 0f;
            } else {
                string[] opLabels = ["==", "!=", "<", "≤", ">", "≥"];
                ImGui.TextDisabled("Compare");
                ImGui.SetNextItemWidth(70f);
                ImGui.Combo("##cfop", ref _condEditOp, opLabels, opLabels.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(CondW - 70f - ImGui.GetStyle().ItemSpacing.X);
                int valI = (int)MathF.Round(_condEditValF);
                ImGui.DragInt("##cfvalf", ref valI, 1f);
                _condEditValF = valI;
            }
        }

        ImGui.Spacing();
        var iconParam = def is { Param: CheckParamKind.ActionId or CheckParamKind.StatusId };
        DrawCondButtons(node => {
            node.CheckField          = _condFieldSelected;
            node.CheckParamId        = _condParamId;
            node.CheckParam2         = _condParam2;
            node.CheckTarget         = _condTarget;
            node.CheckSource         = _condSource;
            node.ConditionCompareOp  = _condEditOp;
            node.ConditionCompareVal = _condEditValF;
            node.IconId              = iconParam ? _condParamIcon : 0;  // status/action icon for the node body
        });
    }

    private void DrawCondButtons(Action<FlowNode> apply) {
        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        ImGui.PushStyleColor(ImGuiCol.Button,        Style.AccentColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Style.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Style.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _condEditNodeId);
            if (node != null) {
                apply(node);
                FlowExecutor.InvalidateFlow(_flow.Id);
                Commit();
            }
            _condEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        ImGui.SameLine();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("Cancel", new Vector2(btnW, 0f))) {
            _condEditNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
    }

    private void BuildCondParamResults(CheckParamKind kind) {
        _condParamResults.Clear();
        var q = _condParamSearch.Trim();
        if (kind == CheckParamKind.ActionId) {
            // Mirror the PvE tab of the action picker: same job-scoped content & filtering.
            var sheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
            if (sheet == null) return;
            var jobScoped = _pickerJobCategoryIds != null;
            foreach (var row in sheet) {
                if (row.IsPvP) continue;                       // PvE tab only
                var assignable = row.IsPlayerAction;
                var catId      = row.ClassJobCategory.RowId;
                if (!assignable) {
                    if (!jobScoped || !_pickerJobExclusiveCategoryIds!.Contains(catId)) continue;
                }
                var name = row.Name.ToString();
                if (name == "") continue;
                if (q.Length > 0 && !name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (jobScoped && assignable && !_pickerJobCategoryIds!.Contains(catId)) continue;
                _condParamResults.Add((row.RowId, name, (uint)row.Icon, row.ClassJobLevel));
                if (_condParamResults.Count >= 200) break;
            }
        } else if (kind == CheckParamKind.StatusId) {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            if (sheet == null) return;
            foreach (var row in sheet) {
                var name = row.Name.ToString();
                if (name == "") continue;
                if (q.Length > 0 && !name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                _condParamResults.Add((row.RowId, name, row.Icon, 0));
                if (_condParamResults.Count >= 200) break;
            }
        }
    }

    private static string ResolveParamName(CheckParamKind kind, uint id) {
        if (id == 0) return "";
        if (kind == CheckParamKind.ActionId) {
            var row = Plugin.DataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(id);
            return row?.Name.ToString() ?? "";
        }
        if (kind == CheckParamKind.StatusId) {
            var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>()?.GetRowOrDefault(id);
            return row?.Name.ToString() ?? "";
        }
        return "";
    }
}
