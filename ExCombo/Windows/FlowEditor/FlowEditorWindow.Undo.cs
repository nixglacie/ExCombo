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
    // ── Undo / redo ──────────────────────────────────────────────────────

    private static string Snapshot(ComboFlow f) => JsonSerializer.Serialize(f, UndoJson);

    // Persist + record an undo point. Replaces every _config.Save() inside the editor: pushes the
    // previously-committed state onto the undo stack, then re-baselines to the new current state.
    private void Commit() {
        if (_flow != null) {
            if (_lastSnapshot != null) {
                _undo.Add(_lastSnapshot);
                var depth = Math.Max(1, Plugin.Config?.UndoDepth ?? 50);
                while (_undo.Count > depth) _undo.RemoveAt(0);
                _redo.Clear();
            }
            _lastSnapshot = Snapshot(_flow);
        }
        _config.Save();
    }

    // Floating Undo/Redo buttons in the canvas top-left corner. Greyed out when nothing to do.
    private void DrawUndoToolbar(Vector2 canvasMin) {
        var start = ImGui.GetCursorPos();
        var icoSz = new Vector2(30f, 28f);

        ImGui.SetCursorScreenPos(canvasMin + new Vector2(8f, 8f));
        bool canUndo = _undo.Count > 0;
        if (!canUndo) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Undo, icoSz)) Undo();
        if (!canUndo) ImGui.EndDisabled();
        if (canUndo && ImGui.IsItemHovered()) ImGui.SetTooltip($"Undo ({_undo.Count})");

        ImGui.SetCursorScreenPos(canvasMin + new Vector2(8f + icoSz.X + 4f, 8f));
        bool canRedo = _redo.Count > 0;
        if (!canRedo) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Redo, icoSz)) Redo();
        if (!canRedo) ImGui.EndDisabled();
        if (canRedo && ImGui.IsItemHovered()) ImGui.SetTooltip($"Redo ({_redo.Count})");

        // Per-flow tuning overrides.
        var slidersPos = canvasMin + new Vector2(8f + (icoSz.X + 4f) * 2f + 6f, 8f);
        _flowCfgAnchor = slidersPos + new Vector2(0f, icoSz.Y + 6f);   // popup opens directly beneath
        ImGui.SetCursorScreenPos(slidersPos);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.SlidersH, icoSz)) ImGui.OpenPopup("Flow settings##excFlowCfg");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Flow tuning overrides");

        // Node reference.
        ImGui.SetCursorScreenPos(slidersPos + new Vector2(icoSz.X + 4f, 0f));
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Book, icoSz)) _wiki.Toggle();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Node wiki");

        ImGui.SetCursorPos(start);
        DrawFlowSettingsPopup();
    }

    // Per-flow tuning overrides: each row can override the global Configuration value or inherit it.
    private Vector2 _flowCfgAnchor;

    private void DrawFlowSettingsPopup() {
        if (_flow == null) return;
        ImGui.SetNextWindowPos(_flowCfgAnchor);   // pinned beneath the sliders button
        if (!ImGui.BeginPopup("Flow settings##excFlowCfg", ImGuiWindowFlags.NoMove)) return;

        ImGui.TextDisabled($"Overrides for \"{_flow.Name}\"");
        ImGui.TextDisabled("Unchecked = inherit global setting.");
        ImGui.Separator();
        var c = Plugin.Config;
        var f = _flow;

        OverrideIntRow("Max weaves / GCD", f.MaxWeavesPerGcd, c?.MaxWeavesPerGcd ?? 2, 1, 3,
            v => { f.MaxWeavesPerGcd = v; _config.Save(); },
            "oGCDs allowed per GCD window for this flow. Most jobs double-weave (2); a few triple-weave.");
        OverrideFloatRow("Anim-lock budget", f.AnimLockBudget, c?.AnimLockBudget ?? 0.6f, 0.3f, 1.0f,
            v => { f.AnimLockBudget = v; _config.Save(); },
            "Assumed animation lock per action. Raise if oGCDs clip your GCD; lower to weave more aggressively.");
        OverrideFloatRow("Queue lead", f.QueueBudget, c?.QueueBudget ?? 0.5f, 0.2f, 1.0f,
            v => { f.QueueBudget = v; _config.Save(); },
            "How early an action may be queued before it's ready. Roughly your ping headroom.");
        OverrideIntRow("Combo grace (ms)", f.ComboGraceMs, c?.ComboGraceMs ?? 500, 100, 1500,
            v => { f.ComboGraceMs = v; _config.Save(); },
            "How long after a press before trusting the game's combo state. Covers buffs applied a frame late.");
        OverrideIntRow("Chain reset (s)", f.ChainResetSeconds, c?.ChainResetSeconds ?? 15, 3, 60,
            v => { f.ChainResetSeconds = v; _config.Save(); },
            "Idle time before an unfinished combo abandons and resets to its trigger.");

        ImGui.Separator();
        DrawDutyScope(f);

        ImGui.EndPopup();
    }

    // Per-flow duty scope: restrict the flow to specific instances (ContentFinderCondition ids).
    // Empty = eligible everywhere; a scoped flow beats an empty-scope fallback on the same trigger
    // when the player is inside one of its duties. Widget shared with the New Flow dialog.
    private void DrawDutyScope(ComboFlow f) {
        ImGui.TextDisabled("Duty scope");
        if (Helpers.DutyScopeUi.Draw($"editflow_{f.Id}", f.DutyScope)) _config.Save();
    }

    private static void OverrideIntRow(string label, int? val, int global, int min, int max, Action<int?> set, string help) {
        bool on = val.HasValue;
        if (ImGui.Checkbox($"##ov_{label}", ref on)) set(on ? val ?? global : null);
        ImGui.SameLine();
        if (!on) ImGui.BeginDisabled();
        int v = val ?? global;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderInt(label, ref v, min, max) && on) set(v);
        if (!on) ImGui.EndDisabled();
        RowHelp(help);
    }

    private static void OverrideFloatRow(string label, float? val, float global, float min, float max, Action<float?> set, string help) {
        bool on = val.HasValue;
        if (ImGui.Checkbox($"##ov_{label}", ref on)) set(on ? val ?? global : null);
        ImGui.SameLine();
        if (!on) ImGui.BeginDisabled();
        float v = val ?? global;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.SliderFloat(label, ref v, min, max, "%.2f") && on) set(v);
        if (!on) ImGui.EndDisabled();
        RowHelp(help);
    }

    // "(?)" marker with a wrapped tooltip, matching the settings window rows.
    private static void RowHelp(string text) {
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(300f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void Undo() {
        if (_flow == null || _undo.Count == 0) return;
        if (_lastSnapshot != null) _redo.Add(_lastSnapshot);
        var snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ApplySnapshot(snap);
    }

    private void Redo() {
        if (_flow == null || _redo.Count == 0) return;
        if (_lastSnapshot != null) _undo.Add(_lastSnapshot);
        var snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        ApplySnapshot(snap);
    }

    // Restore graph state into the existing _flow instance (keeps references in _config.Flows valid).
    private void ApplySnapshot(string snap) {
        if (_flow == null) return;
        var restored = JsonSerializer.Deserialize<ComboFlow>(snap, UndoJson);
        if (restored == null) return;
        _flow.Nodes = restored.Nodes;
        _flow.Edges = restored.Edges;
        _lastSnapshot = snap;
        _selectedNodeIds.Clear();
        FlowExecutor.InvalidateFlow(_flow.Id);
        _config.Save();
    }
}
