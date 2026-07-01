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

public class FlowEditorWindow : Window {
    private readonly Configuration _config;
    private ComboFlow? _flow;
    public string? ActiveFlowId => _flow?.Id;

    private Vector2 _canvasOffset = Vector2.Zero;

    // Undo/redo: JSON snapshots of the flow's graph. _lastSnapshot is the current committed state.
    private static readonly JsonSerializerOptions UndoJson = new();
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _lastSnapshot;

    private string? _wireFromNodeId;      // Mode A: source fixed, dragging to pick a target input
    private int     _wireFromPortIndex;
    private string? _wireToNodeId;        // Mode B: target fixed, dragging to pick a source output

    // Wire dropped on empty canvas → add-node menu open; the created node auto-connects.
    private string? _dropSrcNodeId;       // dangling source (connect new node as its target)
    private int     _dropSrcPort;
    private string? _dropDstNodeId;       // dangling target (connect new node as its source)
    private string? _pickerNodeId;
    private string  _pickerSearch     = "";
    private string  _pickerLastSearch = "\0";
    private readonly List<(uint Id, string Name, uint Icon, byte Level, bool IsPvp)> _pickerResults = new();
    private HashSet<uint>? _pickerJobCategoryIds;
    private HashSet<uint>? _pickerJobExclusiveCategoryIds;
    private bool _pickerPvpTab;
    private string? _branchEditNodeId;
    private int     _branchEditCount;
    private Vector2 _contextMenuCanvasPos;
    private string?       _pendingDeleteNodeId;
    private string?       _confirmDeleteNodeId;    // awaiting single confirm-delete popup
    private List<string>? _pendingDeleteMulti;     // multi-select delete requested (from context menu)
    private List<string>? _confirmDeleteMulti;     // awaiting multi confirm-delete popup
    private string? _draggingNodeId;
    private string? _draggingGroupId;

    private string? _condEditNodeId;
    private string  _condFieldSearch   = "";
    private string  _condFieldSelected = "";
    private int     _condEditOp;
    private int     _condEditVal;

    // Parameterized-condition (Status/Cooldown/Target/Player/Party) edit state.
    private NodeType _condEditType;
    private float    _condEditValF;
    private uint     _condParamId;
    private uint     _condParamIcon;
    private string   _condParamLabel  = "";
    private float    _condParam2;
    private int      _condTarget;
    private string   _condParamSearch = "";
    private readonly List<(uint Id, string Name, uint Icon, byte Level)> _condParamResults = new();
    private string   _condParamLastSearch = "\0";
    private CheckParamKind _condParamLastKind = CheckParamKind.None;

    private string? _noteEditNodeId;
    private string  _noteEditText = "";
    private string? _resizingNoteId;

    // Deferred popup opens — set inside popup contexts, opened outside
    private bool _pendingOpenPicker;
    private bool _pendingOpenBranchEdit;
    private bool _pendingOpenCondEdit;
    private bool _pendingOpenNoteEdit;

    private static readonly Dictionary<string, uint> _jobIconCache = new();

    // Large FontAwesome handle for the Target/Player/Party node glyphs (baked at size → sharp).
    private const float GlyphPx = 32f;
    private IFontHandle? _iconFontLarge;
    private IFontHandle IconFontLarge => _iconFontLarge ??=
        Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = GlyphPx })));

    private readonly HashSet<string> _selectedNodeIds = new();
    private bool    _isMarqueeSelecting;
    private Vector2 _marqueeStart;
    private Vector2 _marqueeEnd;

    // Whole-node clones carry every field (future-proof against new FlowNode props).
    private List<(string OrigId, FlowNode Node, float RelX, float RelY)>? _clipboardNodes;
    private List<(string FromOrig, string ToOrig, int PortIdx)>?          _clipboardEdges;

    private static readonly Vector2 NodeSize    = new(64f, 64f);
    private const           float   PortRadius  = 6f;
    private static          float   GridStep    => Plugin.Config?.GridSize ?? 32f;   // user-tunable
    private const           float   BranchSlotH = 32f;

    // Snap a coordinate to the grid, honouring the SnapToGrid preference.
    private static float Snap(float v) =>
        (Plugin.Config?.SnapToGrid ?? true) ? MathF.Round(v / GridStep) * GridStep : v;

    // Draw an edge between two ports, honouring the WireStyle preference (curved Bézier or straight).
    private static void DrawWire(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float thick) {
        if ((Plugin.Config?.WireStyle ?? WireStyle.Curved) == WireStyle.Straight)
            dl.AddLine(a, b, col, thick);
        else
            dl.AddBezierCubic(a, a + new Vector2(60, 0), b - new Vector2(60, 0), b, col, thick);
    }

    // Midpoint of a wire for the delete button (t=0.5 on the Bézier, or the segment centre if straight).
    private static Vector2 WireMidpoint(Vector2 a, Vector2 b) {
        if ((Plugin.Config?.WireStyle ?? WireStyle.Curved) == WireStyle.Straight)
            return (a + b) * 0.5f;
        var cp1 = a + new Vector2(60, 0);
        var cp2 = b - new Vector2(60, 0);
        return 0.125f * a + 0.375f * cp1 + 0.375f * cp2 + 0.125f * b;
    }

    private void DeleteNode(string nodeId) {
        if (_flow == null) return;
        _selectedNodeIds.Remove(nodeId);
        _flow.Edges.RemoveAll(e => e.FromNodeId == nodeId || e.ToNodeId == nodeId);
        _flow.Nodes.RemoveAll(n => n.Id == nodeId);
        FlowExecutor.InvalidateFlow(_flow.Id);
        Commit();
    }

    private void DeleteSelected(List<string> ids) {
        if (_flow == null || ids.Count == 0) return;
        foreach (var id in ids) {
            _flow.Edges.RemoveAll(e => e.FromNodeId == id || e.ToNodeId == id);
            _flow.Nodes.RemoveAll(n => n.Id == id);
        }
        _selectedNodeIds.Clear();
        FlowExecutor.InvalidateFlow(_flow.Id);
        Commit();
    }

    private void DrawConfirmDeletePopup() {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Delete node?##excDelNode",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;

        ImGui.Text("Delete this node and its connections?");
        ImGui.Spacing();
        if (ImGui.Button("Delete", new Vector2(120f, 0f))) {
            if (_confirmDeleteNodeId != null) DeleteNode(_confirmDeleteNodeId);
            _confirmDeleteNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120f, 0f))) {
            _confirmDeleteNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawConfirmDeleteMultiPopup() {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal("Delete nodes?##excDelMulti",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) return;

        ImGui.Text($"Delete {_confirmDeleteMulti?.Count ?? 0} nodes and their connections?");
        ImGui.Spacing();
        if (ImGui.Button("Delete", new Vector2(120f, 0f))) {
            if (_confirmDeleteMulti != null) DeleteSelected(_confirmDeleteMulti);
            _confirmDeleteMulti = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120f, 0f))) {
            _confirmDeleteMulti = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

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

    private static void DrawDashedRect(ImDrawListPtr dl, Vector2 min, Vector2 max,
        uint col, float thickness = 1f, float dashLen = 6f, float gapLen = 4f, float rounding = 0f) {
        rounding = MathF.Min(rounding, MathF.Min((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f));
        void Dash(Vector2 a, Vector2 b) {
            var dir = Vector2.Normalize(b - a);
            var len = Vector2.Distance(a, b);
            for (var p = 0f; p < len; p += dashLen + gapLen)
                dl.AddLine(a + dir * p, a + dir * MathF.Min(p + dashLen, len), col, thickness);
        }
        void Arc(Vector2 center, float aMin, float aMax) {
            const int Segs = 6;
            var prev = center + new Vector2(MathF.Cos(aMin), MathF.Sin(aMin)) * rounding;
            for (var i = 1; i <= Segs; i++) {
                var a    = aMin + (aMax - aMin) * i / Segs;
                var next = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rounding;
                dl.AddLine(prev, next, col, thickness);
                prev = next;
            }
        }
        if (rounding <= 0f) {
            Dash(new Vector2(min.X, min.Y), new Vector2(max.X, min.Y));
            Dash(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y));
            Dash(new Vector2(max.X, max.Y), new Vector2(min.X, max.Y));
            Dash(new Vector2(min.X, max.Y), new Vector2(min.X, min.Y));
        } else {
            var r = rounding;
            Dash(new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y));
            Arc(new Vector2(max.X - r, min.Y + r), -MathF.PI * 0.5f, 0f);
            Dash(new Vector2(max.X, min.Y + r), new Vector2(max.X, max.Y - r));
            Arc(new Vector2(max.X - r, max.Y - r), 0f, MathF.PI * 0.5f);
            Dash(new Vector2(max.X - r, max.Y), new Vector2(min.X + r, max.Y));
            Arc(new Vector2(min.X + r, max.Y - r), MathF.PI * 0.5f, MathF.PI);
            Dash(new Vector2(min.X, max.Y - r), new Vector2(min.X, min.Y + r));
            Arc(new Vector2(min.X + r, min.Y + r), MathF.PI, MathF.PI * 1.5f);
        }
    }

    private static float NodeHeight(FlowNode n) =>
        n.Type == NodeType.Note
            ? n.NoteH
            : n.Type == NodeType.Branch || FlowNode.IsGate(n.Type)
                ? MathF.Max(NodeSize.Y, BranchSlotH * n.OutputCount)
                : NodeSize.Y;

    private const float NoteMinW = 96f;
    private const float NoteMinH = 64f;
    private const float CondW    = 360f;   // content width for the content-sized condition modal
    private static float NodeWidthOf(FlowNode n) => n.Type == NodeType.Note ? n.NoteW : NodeSize.X;

    // Screen position of a node's output port (branch/gate have OutputCount ports; others have one).
    private Vector2 OutputPortPos(FlowNode n, int port, Vector2 canvasMin) =>
        n.Type == NodeType.Branch || FlowNode.IsGate(n.Type)
            ? canvasMin + _canvasOffset + new Vector2(n.X + NodeSize.X, (port + 0.5f) * BranchSlotH + n.Y)
            : canvasMin + _canvasOffset + new Vector2(n.X + NodeSize.X, n.Y + NodeSize.Y * 0.5f);

    // The output port under a screen point, if any (Notes have no output).
    private (string NodeId, int Port)? FindOutputPortAt(Vector2 pt, Vector2 canvasMin) {
        foreach (var n in _flow!.Nodes) {
            if (n.Type == NodeType.Note) continue;
            var ports = n.Type == NodeType.Branch || FlowNode.IsGate(n.Type) ? n.OutputCount : 1;
            for (var p = 0; p < ports; p++)
                if (Vector2.Distance(pt, OutputPortPos(n, p, canvasMin)) < PortRadius * 3f)
                    return (n.Id, p);
        }
        return null;
    }

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
    private static uint Bg1 => Col(0.102f, 0.106f, 0.118f);

    public FlowEditorWindow(Configuration config) : base("Flow Editor###ExComboEditor") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
        // Panning places node hit-boxes off-canvas; without this the window grows scrollable space.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        _config = config;
    }

    public void Dispose() {
        _iconFontLarge?.Dispose();
        _iconFontLarge = null;
    }

    public void SetFlow(ComboFlow flow) {
        _flow         = flow;
        _canvasOffset = new Vector2(flow.ViewX, flow.ViewY);
        _selectedNodeIds.Clear();
        _undo.Clear();
        _redo.Clear();
        _lastSnapshot = Snapshot(flow);
        WindowName    = $"Flow Editor — {flow.Name}###ExComboEditor";
    }

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

        ImGui.EndPopup();
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

    public override void PreDraw() {
        Style.Push();
        // Canvas fills the window (nodes draw on the draw list) — no window padding needed.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }
    public override void PostDraw() {
        ImGui.PopStyleVar();
        Style.Pop();
    }

    public override void Draw() {
        if (_flow == null) { ImGui.TextDisabled("No flow selected."); return; }

        // Off-canvas node hit-boxes can push scroll; pin it so panning never scrolls the window.
        if (ImGui.GetScrollX() != 0f) ImGui.SetScrollX(0f);
        if (ImGui.GetScrollY() != 0f) ImGui.SetScrollY(0f);

        // The window itself is flush (0 padding, set in PreDraw); popups & menus spawned from here
        // get half the usual padding back.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(7f, 6f));

        var dl         = ImGui.GetWindowDrawList();
        var canvasMin  = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        var canvasMax  = canvasMin + canvasSize;

        // ── Background ────────────────────────────────────────────────────
        dl.AddRectFilled(canvasMin, canvasMax, Bg1);
        dl.AddRect(canvasMin, canvasMax, Col(0.333f, 0.353f, 0.388f));

        // ── Grid ──────────────────────────────────────────────────────────
        var gridCol = Col(0.173f, 0.180f, 0.200f, 0.6f);
        for (var x = _canvasOffset.X % GridStep; x < canvasSize.X; x += GridStep)
            dl.AddLine(canvasMin + new Vector2(x, 0), canvasMin + new Vector2(x, canvasSize.Y), gridCol);
        for (var y = _canvasOffset.Y % GridStep; y < canvasSize.Y; y += GridStep)
            dl.AddLine(canvasMin + new Vector2(0, y), canvasMin + new Vector2(canvasSize.X, y), gridCol);

        // ── Middle-drag pan (always active) ──────────────────────────────
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            _canvasOffset += ImGui.GetIO().MouseDelta;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle) && _flow is { } panFlow
            && (panFlow.ViewX != _canvasOffset.X || panFlow.ViewY != _canvasOffset.Y)) {
            panFlow.ViewX = _canvasOffset.X;   // persist view without an undo entry
            panFlow.ViewY = _canvasOffset.Y;
            _config.Save();
        }

        var mouse2 = ImGui.GetMousePos();

        // ── Edges ─────────────────────────────────────────────────────────
        FlowEdge? edgeToDelete = null;
        foreach (var edge in _flow.Edges) {
            var fn = _flow.Nodes.Find(n => n.Id == edge.FromNodeId);
            var tn = _flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if (fn == null || tn == null) continue;

            Vector2 p1;
            if (fn.Type == NodeType.Branch || FlowNode.IsGate(fn.Type)) {
                var slotY = fn.Y + (edge.FromPortIndex + 0.5f) * BranchSlotH;
                p1 = canvasMin + _canvasOffset + new Vector2(fn.X + NodeSize.X, slotY);
            } else {
                p1 = canvasMin + _canvasOffset + new Vector2(fn.X + NodeSize.X, fn.Y + NodeSize.Y * 0.5f);
            }
            var p4  = canvasMin + _canvasOffset + new Vector2(tn.X, tn.Y + NodeHeight(tn) * 0.5f);
            // Gate (condition) edges are colored by branch: port 0 = true (green), port 1 = false (red).
            var edgeCol = FlowNode.IsGate(fn.Type)
                ? (edge.FromPortIndex == 0 ? Col(0.30f, 0.80f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f))
                : Col(0.4f, 0.6f, 1f, 0.85f);
            DrawWire(dl, p1, p4, edgeCol, 2f);

            // ── Delete button at midpoint ─────────────────────────────────
            var mid     = WireMidpoint(p1, p4);
            const float Br = 7f;
            const float Bx = 3.5f;
            var btnHovered = _wireFromNodeId == null && _wireToNodeId == null && Vector2.Distance(mouse2, mid) < Br;
            dl.AddCircleFilled(mid, Br, btnHovered ? Col(0.75f, 0.15f, 0.15f) : Col(0.18f, 0.18f, 0.22f));
            dl.AddCircle(mid, Br, btnHovered ? Col(1f, 0.4f, 0.4f) : Col(0.45f, 0.35f, 0.35f), 12, 1.5f);
            dl.AddLine(mid + new Vector2(-Bx, -Bx), mid + new Vector2(Bx, Bx), Col(1f, 1f, 1f, 0.9f), 1.5f);
            dl.AddLine(mid + new Vector2(Bx, -Bx), mid + new Vector2(-Bx, Bx), Col(1f, 1f, 1f, 0.9f), 1.5f);
            if (btnHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) edgeToDelete = edge;

            // ── Re-wire: grab an endpoint to detach and drag it elsewhere ─────
            if (_wireFromNodeId == null && _wireToNodeId == null && !btnHovered) {
                var grabEnd   = Vector2.Distance(mouse2, p4) < PortRadius * 2.5f;   // target/input end
                var grabStart = Vector2.Distance(mouse2, p1) < PortRadius * 2.5f;   // source/output end
                if (grabEnd || grabStart) {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                        edgeToDelete = edge;                       // detach the edge
                        if (grabEnd) { _wireFromNodeId = edge.FromNodeId; _wireFromPortIndex = edge.FromPortIndex; }
                        else         { _wireToNodeId   = edge.ToNodeId; }
                    }
                }
            }
        }
        if (edgeToDelete != null) {
            _flow.Edges.Remove(edgeToDelete);
            FlowExecutor.InvalidateFlow(_flow.Id);
            Commit();
        }

        // ── Pending wire ──────────────────────────────────────────────────
        if (_wireFromNodeId != null) {
            var wireMouse = ImGui.GetMousePos();
            var wfn       = _flow.Nodes.Find(n => n.Id == _wireFromNodeId);
            if (wfn != null) {
                Vector2 p1;
                if (wfn.Type == NodeType.Branch || FlowNode.IsGate(wfn.Type)) {
                    var slotY = wfn.Y + (_wireFromPortIndex + 0.5f) * BranchSlotH;
                    p1 = canvasMin + _canvasOffset + new Vector2(wfn.X + NodeSize.X, slotY);
                } else {
                    p1 = canvasMin + _canvasOffset + new Vector2(wfn.X + NodeSize.X, wfn.Y + NodeSize.Y * 0.5f);
                }
                var wireEnd = wireMouse;
                foreach (var t in _flow.Nodes) {
                    if (t.Id == _wireFromNodeId) continue;
                    if (t.Type == NodeType.Trigger) continue;
                    var tip = canvasMin + _canvasOffset + new Vector2(t.X, t.Y + NodeHeight(t) * 0.5f);
                    if (Vector2.Distance(wireMouse, tip) < PortRadius * 3f) { wireEnd = tip; break; }
                }
                var wireCol = FlowNode.IsGate(wfn.Type)
                    ? (_wireFromPortIndex == 0 ? Col(0.30f, 0.80f, 0.30f, 0.6f) : Col(0.85f, 0.30f, 0.30f, 0.6f))
                    : Col(0.4f, 0.6f, 1f, 0.5f);
                DrawWire(dl, p1, wireEnd, wireCol, 2f);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                var connected = false;
                foreach (var t in _flow.Nodes) {
                    if (t.Id == _wireFromNodeId) continue;
                    if (t.Type == NodeType.Trigger) continue;
                    var tip = canvasMin + _canvasOffset + new Vector2(t.X, t.Y + NodeHeight(t) * 0.5f);
                    if (Vector2.Distance(wireMouse, tip) < PortRadius * 3f) {
                        // prevent duplicate edge on same port
                        if (!_flow.Edges.Exists(e => e.FromNodeId == _wireFromNodeId
                                                   && e.FromPortIndex == _wireFromPortIndex
                                                   && e.ToNodeId == t.Id)) {
                            _flow.Edges.RemoveAll(e => e.FromNodeId == _wireFromNodeId
                                                    && e.FromPortIndex == _wireFromPortIndex);
                            _flow.Edges.Add(new FlowEdge {
                                FromNodeId    = _wireFromNodeId,
                                ToNodeId      = t.Id,
                                FromPortIndex = _wireFromPortIndex,
                            });
                            FlowExecutor.InvalidateFlow(_flow.Id);
                            Commit();
                        }
                        connected = true;
                        break;
                    }
                }
                // Dropped on empty space → offer the add-node menu; the new node wires as the target.
                if (!connected) {
                    _dropSrcNodeId        = _wireFromNodeId;
                    _dropSrcPort          = _wireFromPortIndex;
                    _contextMenuCanvasPos = wireMouse - canvasMin - _canvasOffset;
                    ImGui.OpenPopup("##canvas_ctx");
                }
                _wireFromNodeId = null;
            }
        }

        // ── Pending wire (Mode B: target fixed, pick a source output) ─────
        if (_wireToNodeId != null) {
            var ttn = _flow.Nodes.Find(n => n.Id == _wireToNodeId);
            if (ttn == null) {
                _wireToNodeId = null;
            } else {
                var p4b   = canvasMin + _canvasOffset + new Vector2(ttn.X, ttn.Y + NodeHeight(ttn) * 0.5f);
                var hit   = FindOutputPortAt(mouse2, canvasMin);
                var start = hit is { } h && h.NodeId != _wireToNodeId
                    ? OutputPortPos(_flow.Nodes.Find(n => n.Id == h.NodeId)!, h.Port, canvasMin)
                    : mouse2;
                DrawWire(dl, start, p4b, Col(0.4f, 0.6f, 1f, 0.5f), 2f);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                    if (hit is { } hh && hh.NodeId != _wireToNodeId) {
                        if (!_flow.Edges.Exists(e => e.FromNodeId == hh.NodeId && e.FromPortIndex == hh.Port && e.ToNodeId == _wireToNodeId)) {
                            _flow.Edges.RemoveAll(e => e.FromNodeId == hh.NodeId && e.FromPortIndex == hh.Port);
                            _flow.Edges.Add(new FlowEdge { FromNodeId = hh.NodeId, ToNodeId = _wireToNodeId, FromPortIndex = hh.Port });
                            FlowExecutor.InvalidateFlow(_flow.Id);
                            Commit();
                        }
                    } else {
                        // Dropped on empty space → add-node menu; the new node wires as the source.
                        _dropDstNodeId        = _wireToNodeId;
                        _contextMenuCanvasPos = mouse2 - canvasMin - _canvasOffset;
                        ImGui.OpenPopup("##canvas_ctx");
                    }
                    _wireToNodeId = null;
                }
            }
        }

        // ── Dismantle any group left with a single node ───────────────────
        var groupCounts = new Dictionary<string, int>();
        foreach (var n in _flow.Nodes)
            if (n.GroupId != null) groupCounts[n.GroupId] = groupCounts.GetValueOrDefault(n.GroupId) + 1;
        var prunedGroup = false;
        foreach (var n in _flow.Nodes)
            if (n.GroupId != null && groupCounts[n.GroupId] < 2) { n.GroupId = null; prunedGroup = true; }
        if (prunedGroup) { FlowExecutor.InvalidateFlow(_flow.Id); Commit(); }

        // ── Combo group boxes (behind everything) ─────────────────────────
        var groupIds = new HashSet<string>();
        foreach (var n in _flow.Nodes)
            if (n.GroupId != null) groupIds.Add(n.GroupId);
        foreach (var gid in groupIds) {
            var gx = float.MaxValue; var gy = float.MaxValue;
            var gx2 = float.MinValue; var gy2 = float.MinValue;
            var gany = false;
            foreach (var n in _flow.Nodes) {
                if (n.GroupId != gid) continue;
                if (n.X < gx) gx = n.X;  if (n.Y < gy) gy = n.Y;
                var nx = n.X + NodeSize.X; var ny = n.Y + NodeHeight(n);
                if (nx > gx2) gx2 = nx;   if (ny > gy2) gy2 = ny;
                gany = true;
            }
            if (!gany) continue;
            const float GPad = 11f;
            const float GTop = 38f;
            const float GBot = 17f; // extra room for the chain badge that hangs below the node's bottom edge
            var gMin = canvasMin + _canvasOffset + new Vector2(gx - GPad, gy - GTop);
            var gMax = canvasMin + _canvasOffset + new Vector2(gx2 + GPad, gy2 + GBot);
            dl.AddRectFilled(gMin, gMax, Style.ComboU32(0.08f), 8f);
            dl.AddRect(gMin, gMax, Style.ComboU32(0.7f), 8f, ImDrawFlags.None, 2f);
            dl.AddText(gMin + new Vector2(8f, 6f), Style.ComboU32(0.95f), "Combo");

            // Header strip = drag handle: grab it to move the whole group.
            ImGui.SetCursorScreenPos(gMin);
            ImGui.InvisibleButton($"##grp_{gid}", new Vector2(gMax.X - gMin.X, GTop));
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) {
                _draggingGroupId = gid;
                var gd = ImGui.GetIO().MouseDelta;
                foreach (var n in _flow.Nodes)
                    if (n.GroupId == gid) { n.X += gd.X; n.Y += gd.Y; }
            }
            if (ImGui.IsItemDeactivated() && _draggingGroupId == gid) {
                foreach (var n in _flow.Nodes)
                    if (n.GroupId == gid) {
                        n.X = Snap(n.X);
                        n.Y = Snap(n.Y);
                    }
                _draggingGroupId = null;
                Commit();
            }
        }

        // ── Selection envelope (behind nodes) ─────────────────────────────
        if (_selectedNodeIds.Count > 0) {
            var ex = float.MaxValue; var ey = float.MaxValue;
            var ex2 = float.MinValue; var ey2 = float.MinValue;
            var any = false;
            foreach (var n in _flow.Nodes) {
                if (!_selectedNodeIds.Contains(n.Id)) continue;
                if (n.X < ex) ex = n.X;  if (n.Y < ey) ey = n.Y;
                var nx = n.X + NodeWidthOf(n); var ny = n.Y + NodeHeight(n);
                if (nx > ex2) ex2 = nx;   if (ny > ey2) ey2 = ny;
                any = true;
            }
            if (any) {
                const float PadH = 6f;
                const float PadTop = 20f;
                var eMin = canvasMin + _canvasOffset + new Vector2(ex - PadH, ey - PadTop);
                var eMax = canvasMin + _canvasOffset + new Vector2(ex2 + PadH, ey2 + PadH);
                dl.AddRectFilled(eMin, eMax, Style.AccentU32(0.2f), 6f);
                DrawDashedRect(dl, eMin, eMax, Style.AccentU32(0.9f), 1.5f, 6f, 4f, 6f);
            }
        }

        // ── Nodes ─────────────────────────────────────────────────────────
        var anyNodeRightClicked = false;

        foreach (var node in _flow.Nodes) {
            var isTrigger   = node.Type == NodeType.Trigger;
            var isBranch    = node.Type == NodeType.Branch;
            var isGate      = FlowNode.IsGate(node.Type);
            var isJobCond   = node.Type == NodeType.Condition;
            var isNote      = node.Type == NodeType.Note;
            var nodeH       = NodeHeight(node);
            var nodeW       = NodeWidthOf(node);
            var sp          = canvasMin + _canvasOffset + new Vector2(node.X, node.Y);
            var inPort      = sp + new Vector2(0f, nodeH * 0.5f);

            // Input port is grabbable to start a wire the other way (input → output, Mode B).
            var overInStart = !isTrigger && !isNote
                && _wireFromNodeId == null && _wireToNodeId == null
                && Vector2.Distance(mouse2, inPort) < PortRadius * 2f;

            // ── Output port hover detection ───────────────────────────────
            bool overOutPort    = false;
            int  overOutPortIdx = 0;
            bool overNoteResize = false;
            if (isNote) {
                // Note nodes are pure comments — no ports; bottom-right corner = resize handle.
                if (_wireFromNodeId == null)
                    overNoteResize = Vector2.Distance(mouse2, sp + new Vector2(nodeW, nodeH)) < 12f;
            } else if (isBranch || isGate) {
                if (_wireFromNodeId == null && _wireToNodeId == null) {
                    for (var p = 0; p < node.OutputCount; p++) {
                        var portPos = sp + new Vector2(NodeSize.X, (p + 0.5f) * BranchSlotH);
                        if (Vector2.Distance(mouse2, portPos) < PortRadius * 2f) {
                            overOutPort    = true;
                            overOutPortIdx = p;
                            break;
                        }
                    }
                }
            } else {
                var outPort = sp + new Vector2(NodeSize.X, NodeSize.Y * 0.5f);
                overOutPort = _wireFromNodeId == null && _wireToNodeId == null && Vector2.Distance(mouse2, outPort) < PortRadius * 2f;
            }

            ImGui.SetCursorScreenPos(sp);
            ImGui.InvisibleButton($"node_{node.Id}", new Vector2(nodeW, nodeH));
            var nodeHovered = ImGui.IsItemHovered();
            var nodeActive  = ImGui.IsItemActive();

            // ── Select on click ───────────────────────────────────────────
            if (nodeHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !overOutPort && !overInStart) {
                var xBtn = sp + new Vector2(NodeSize.X - 7f, 7f);
                if (Vector2.Distance(mouse2, xBtn) >= 7f) {
                    if (ImGui.GetIO().KeyCtrl)
                        { if (!_selectedNodeIds.Remove(node.Id)) _selectedNodeIds.Add(node.Id); }
                    else if (!_selectedNodeIds.Contains(node.Id))
                        { _selectedNodeIds.Clear(); _selectedNodeIds.Add(node.Id); }
                }
            }

            // ── Note resize (bottom-right handle) ─────────────────────────
            if (overNoteResize && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _resizingNoteId = node.Id;
            if (_resizingNoteId == node.Id) {
                if (ImGui.IsMouseDragging(ImGuiMouseButton.Left)) {
                    var rd = ImGui.GetIO().MouseDelta;
                    node.NoteW = MathF.Max(NoteMinW, node.NoteW + rd.X);
                    node.NoteH = MathF.Max(NoteMinH, node.NoteH + rd.Y);
                }
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                    node.NoteW = MathF.Max(NoteMinW, Snap(node.NoteW));
                    node.NoteH = MathF.Max(NoteMinH, Snap(node.NoteH));
                    _resizingNoteId = null;
                    Commit();
                }
            }

            // ── Drag ──────────────────────────────────────────────────────
            if (nodeActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left)
                && _wireFromNodeId == null && _wireToNodeId == null && _resizingNoteId != node.Id && !overNoteResize) {
                _draggingNodeId = node.Id;
                var delta = ImGui.GetIO().MouseDelta;
                if (_selectedNodeIds.Contains(node.Id)) {
                    foreach (var selId in _selectedNodeIds) {
                        var sn = _flow.Nodes.Find(n => n.Id == selId);
                        if (sn != null) { sn.X += delta.X; sn.Y += delta.Y; }
                    }
                } else {
                    node.X += delta.X; node.Y += delta.Y;
                }
            }
            if (ImGui.IsItemDeactivated() && _draggingNodeId == node.Id) {
                if (_selectedNodeIds.Contains(node.Id)) {
                    foreach (var selId in _selectedNodeIds) {
                        var sn = _flow.Nodes.Find(n => n.Id == selId);
                        if (sn != null) {
                            sn.X = Snap(sn.X);
                            sn.Y = Snap(sn.Y);
                        }
                    }
                } else {
                    node.X = Snap(node.X);
                    node.Y = Snap(node.Y);
                }
                _draggingNodeId = null;
                Commit();
            }

            // ── Wire start ────────────────────────────────────────────────
            if (overOutPort && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                _wireFromNodeId    = node.Id;
                _wireFromPortIndex = (isBranch || isGate) ? overOutPortIdx : 0;
            }
            // Input → output: grab an input port to pick a source (Mode B); same node is blocked.
            if (overInStart && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _wireToNodeId = node.Id;

            if (nodeHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
                if (isBranch)    OpenBranchEdit(node.Id, node.OutputCount);
                else if (isGate) OpenConditionEdit(node.Id);
                else if (isNote) OpenNoteEdit(node.Id);
                else             OpenPicker(node.Id);
            }

            if (nodeHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
                if (_selectedNodeIds.Count > 1 && _selectedNodeIds.Contains(node.Id))
                    ImGui.OpenPopup("##multi_node_ctx");
                else
                    ImGui.OpenPopup($"node_ctx_{node.Id}");
                anyNodeRightClicked = true;
            }

            // ── Draw node ─────────────────────────────────────────────────
            var isSelected = _selectedNodeIds.Contains(node.Id);
            if (isNote) {
                // ── Note node draw (pure comment, no ports) ───────────────
                var noteAccent = Style.NodeColU32(NodeType.Note);
                var borderCol  = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? noteAccent
                        : Style.NodeColU32(NodeType.Note, 0.45f);
                dl.AddRectFilled(sp, sp + new Vector2(nodeW, nodeH), Col(0.12f, 0.12f, 0.13f, 0.95f), 6f);
                dl.AddRect(sp, sp + new Vector2(nodeW, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                var noteTitle = "Note";
                var noteTitleW = ImGui.CalcTextSize(noteTitle).X;
                DrawHelpers.DrawText(dl, sp + new Vector2((nodeW - noteTitleW) * 0.5f, -16f), noteTitle, noteAccent, true);

                var noteText = node.NoteText != "" ? node.NoteText : "Double-click to edit note";
                var noteCol  = node.NoteText != "" ? Col(0.96f, 0.96f, 0.97f) : Col(0.55f, 0.56f, 0.58f);
                const float tPad = 6f;
                dl.PushClipRect(sp + new Vector2(tPad, tPad), sp + new Vector2(nodeW - tPad, nodeH - tPad), true);
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), sp + new Vector2(tPad, tPad),
                    noteCol, noteText, nodeW - 2f * tPad);
                dl.PopClipRect();

                // Delete button (top-right)
                if (nodeHovered) {
                    var xBtn = sp + new Vector2(nodeW - 7f, 7f);
                    const float XBr = 7f;
                    const float XS  = 3f;
                    var xHov = Vector2.Distance(mouse2, xBtn) < XBr;
                    dl.AddCircleFilled(xBtn, XBr, xHov ? Col(0.75f, 0.15f, 0.15f) : Col(0.20f, 0.08f, 0.08f, 0.9f));
                    dl.AddCircle(xBtn, XBr, Col(1f, 0.3f, 0.3f, 0.8f), 12, 1.2f);
                    dl.AddLine(xBtn + new Vector2(-XS, -XS), xBtn + new Vector2(XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    dl.AddLine(xBtn + new Vector2(XS, -XS), xBtn + new Vector2(-XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    if (xHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        _pendingDeleteNodeId = node.Id;
                }

                // Resize handle (bottom-right corner)
                if (overNoteResize || _resizingNoteId == node.Id) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
                var hc = sp + new Vector2(nodeW, nodeH);
                var handleCol = overNoteResize || _resizingNoteId == node.Id ? noteAccent : Style.NodeColU32(NodeType.Note, 0.5f);
                dl.AddTriangleFilled(hc + new Vector2(-12f, -2f), hc + new Vector2(-2f, -2f), hc + new Vector2(-2f, -12f), handleCol);
            } else if (isBranch) {
                var borderCol = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? Style.NodeColU32(node.Type)
                        : Style.NodeColU32(node.Type, 0.5f);
                dl.AddRectFilled(sp, sp + new Vector2(NodeSize.X, nodeH), Col(0.08f, 0.05f, 0.12f), 6f);
                dl.AddRect(sp, sp + new Vector2(NodeSize.X, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                var label      = "Priority";
                var labelWidth = ImGui.CalcTextSize(label).X;
                var labelPos   = sp + new Vector2((NodeSize.X - labelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, labelPos, label, Style.NodeColU32(node.Type), true);

                // Input port (left midpoint)
                var overInPort = overInStart || (_wireFromNodeId != null && _wireFromNodeId != node.Id
                    && Vector2.Distance(mouse2, inPort) < PortRadius * 3f);
                dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.55f, 0.80f, 1f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                // Output ports
                for (var p = 0; p < node.OutputCount; p++) {
                    var portPos     = sp + new Vector2(NodeSize.X, (p + 0.5f) * BranchSlotH);
                    var portHovered = (overOutPort && overOutPortIdx == p)
                        || (_wireToNodeId != null && node.Id != _wireToNodeId
                            && Vector2.Distance(mouse2, portPos) < PortRadius * 3f);   // Mode B source hover
                    dl.AddCircleFilled(portPos, PortRadius,
                        portHovered ? Col(0.6f, 0.8f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(portPos, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                    // Port label (1-based) to the left of port
                    var numLabel = (p + 1).ToString();
                    var numW     = ImGui.CalcTextSize(numLabel).X;
                    DrawHelpers.DrawText(dl, portPos + new Vector2(-numW - PortRadius - 4f, -7f),
                        numLabel, Style.NodeColU32(node.Type, 0.8f), false);

                    // Divider line between slots (except after last)
                    if (p < node.OutputCount - 1) {
                        var lineY = sp.Y + (p + 1) * BranchSlotH;
                        dl.AddLine(new Vector2(sp.X + 4f, lineY), new Vector2(sp.X + NodeSize.X - 4f, lineY),
                            Style.NodeColU32(node.Type, 0.2f), 1f);
                    }
                }

                // Delete button (top-right)
                if (nodeHovered) {
                    var xBtn = sp + new Vector2(NodeSize.X - 7f, 7f);
                    const float XBr = 7f;
                    const float XS  = 3f;
                    var xHov = Vector2.Distance(mouse2, xBtn) < XBr;
                    dl.AddCircleFilled(xBtn, XBr, xHov ? Col(0.75f, 0.15f, 0.15f) : Col(0.20f, 0.08f, 0.08f, 0.9f));
                    dl.AddCircle(xBtn, XBr, Col(1f, 0.3f, 0.3f, 0.8f), 12, 1.2f);
                    dl.AddLine(xBtn + new Vector2(-XS, -XS), xBtn + new Vector2(XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    dl.AddLine(xBtn + new Vector2(XS, -XS), xBtn + new Vector2(-XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    if (xHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        _pendingDeleteNodeId = node.Id;
                }
            } else if (isGate) {
                // ── Condition-family node draw (all gate types) ───────────
                var condAccent = Style.NodeColU32(node.Type);
                var borderCol  = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? condAccent
                        : Style.NodeColU32(node.Type, 0.5f);
                dl.AddRectFilled(sp, sp + new Vector2(NodeSize.X, nodeH), Col(0.12f, 0.08f, 0.03f), 6f);

                // Body icon: legacy job gauge = job icon; Status/Cooldown = picked status/action icon;
                // Target/Player/Party = a category glyph.
                if (isJobCond) {
                    var jobIconId = GetJobIconId(_flow!.Job);
                    if (jobIconId != 0) {
                        var tex = Plugin.TextureProvider
                            .GetFromGameIcon(new GameIconLookup(jobIconId))?.GetWrapOrDefault();
                        if (tex != null)
                            DrawHelpers.DrawIcon(dl, tex, sp, new Vector2(NodeSize.X, nodeH), 1f, 6f);
                    }
                } else if (node.Type is NodeType.StatusCondition or NodeType.CooldownCondition && node.IconId != 0) {  // (is-or) && bool
                    var tex = Plugin.TextureProvider
                        .GetFromGameIcon(new GameIconLookup(node.IconId))?.GetWrapOrDefault();
                    if (tex != null) {
                        // Aspect-fit centered in the body (status icons aren't square).
                        var scale = MathF.Min(NodeSize.X / tex.Width, nodeH / tex.Height);
                        var isz   = new Vector2(tex.Width * scale, tex.Height * scale);
                        // Status art has transparent top padding — nudge it down a few px to sit centered.
                        var iyAdj = node.Type == NodeType.StatusCondition ? 3.5f : 0f;
                        var ioff  = new Vector2((NodeSize.X - isz.X) * 0.5f, (nodeH - isz.Y) * 0.5f + iyAdj);
                        DrawHelpers.DrawIcon(dl, tex, sp + ioff, isz, 1f, 6f);
                    }
                } else if (node.Type is NodeType.TargetCondition or NodeType.PlayerCondition or NodeType.PartyCondition) {
                    var glyph = node.Type switch {
                        NodeType.TargetCondition => FontAwesomeIcon.Crosshairs,
                        NodeType.PlayerCondition => FontAwesomeIcon.User,
                        _                        => FontAwesomeIcon.Users,
                    };
                    var gstr = glyph.ToIconString();
                    using (IconFontLarge.Push()) {
                        var font = ImGui.GetFont();
                        var sz   = ImGui.GetFontSize();
                        var gsz  = ImGui.CalcTextSize(gstr);
                        dl.AddText(font, sz, sp + new Vector2((NodeSize.X - gsz.X) * 0.5f, (nodeH - gsz.Y) * 0.5f),
                            Style.NodeColU32(node.Type, 0.85f), gstr);
                    }
                }

                dl.AddRect(sp, sp + new Vector2(NodeSize.X, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                // Live condition inspector: tint an outer ring green/red by current eval, in combat only.
                if (_config.ShowConditionState && _flow is { } inspFlow && Helpers.PlayerStateHelper.InCombat()) {
                    bool pass = FlowExecutor.EvalGate(inspFlow, node);
                    var tint  = pass ? Col(0.30f, 0.85f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f);
                    dl.AddRect(sp - new Vector2(3f, 3f), sp + new Vector2(NodeSize.X + 3f, nodeH + 3f),
                        tint, 8f, ImDrawFlags.None, 2f);
                }

                var condLabel      = GateNodeLabel(node);
                var condLabelWidth = ImGui.CalcTextSize(condLabel).X;
                var condLabelPos   = sp + new Vector2((NodeSize.X - condLabelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, condLabelPos, condLabel, condAccent, true);

                // Input port
                var overInPort = overInStart || (_wireFromNodeId != null && _wireFromNodeId != node.Id
                    && Vector2.Distance(mouse2, inPort) < PortRadius * 3f);
                dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.55f, 0.80f, 1f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                // Output ports: port 0 = true (green), port 1 = false (red)
                for (var p = 0; p < node.OutputCount; p++) {
                    var portPos     = sp + new Vector2(NodeSize.X, (p + 0.5f) * BranchSlotH);
                    var portHovered = (overOutPort && overOutPortIdx == p)
                        || (_wireToNodeId != null && node.Id != _wireToNodeId
                            && Vector2.Distance(mouse2, portPos) < PortRadius * 3f);   // Mode B source hover
                    var ring = p == 0
                        ? (portHovered ? Col(0.45f, 1f, 0.45f) : Col(0.30f, 0.78f, 0.30f))
                        : (portHovered ? Col(1f, 0.45f, 0.45f) : Col(0.80f, 0.28f, 0.28f));
                    dl.AddCircleFilled(portPos, PortRadius, portHovered ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(portPos, PortRadius, ring, 12, 1.5f);
                }

                // Delete button (top-right)
                if (nodeHovered) {
                    var xBtn = sp + new Vector2(NodeSize.X - 7f, 7f);
                    const float XBr = 7f;
                    const float XS  = 3f;
                    var xHov = Vector2.Distance(mouse2, xBtn) < XBr;
                    dl.AddCircleFilled(xBtn, XBr, xHov ? Col(0.75f, 0.15f, 0.15f) : Col(0.20f, 0.08f, 0.08f, 0.9f));
                    dl.AddCircle(xBtn, XBr, Col(1f, 0.3f, 0.3f, 0.8f), 12, 1.2f);
                    dl.AddLine(xBtn + new Vector2(-XS, -XS), xBtn + new Vector2(XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    dl.AddLine(xBtn + new Vector2(XS, -XS), xBtn + new Vector2(-XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    if (xHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        _pendingDeleteNodeId = node.Id;
                }
            } else {
                // ── Trigger / Action node draw ────────────────────────────
                var outPort   = sp + new Vector2(NodeSize.X, NodeSize.Y * 0.5f);
                var na        = Style.NodeColor(node.Type);
                var accentR   = na.X;
                var accentG   = na.Y;
                var accentB   = na.Z;
                var bgCol     = isTrigger ? Col(0.09f, 0.13f, 0.10f) : Col(0.09f, 0.11f, 0.16f);
                var borderCol = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? Col(accentR, accentG, accentB)
                        : Col(accentR, accentG, accentB, 0.5f);
                dl.AddRectFilled(sp, sp + NodeSize, bgCol, 6f);

                if (node.IconId != 0) {
                    var tex = Plugin.TextureProvider
                        .GetFromGameIcon(new GameIconLookup(node.IconId))?.GetWrapOrDefault();
                    if (tex != null)
                        DrawHelpers.DrawIcon(dl, tex, sp, NodeSize, 1f, 6f);
                }

                dl.AddRect(sp, sp + NodeSize, borderCol, 6f, ImDrawFlags.None, isSelected || nodeHovered ? 2f : 1.5f);

                // Status badges — centered on the bottom edge (half below). oGCD = lightning,
                // combo-group = chain. When a node has both, lay them out as a centered pair.
                if (!isTrigger && (node.IsOgcd || node.GroupId != null || node.RetargetMode != 0)) {
                    ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
                    const float pad = 3f, gap = 4f;
                    var darkCol = Col(0.07f, 0.08f, 0.11f, 1f);

                    var boltStr = FontAwesomeIcon.Bolt.ToIconString();
                    var linkStr = FontAwesomeIcon.Link.ToIconString();
                    var rtgStr  = FontAwesomeIcon.Crosshairs.ToIconString();
                    var boltGsz = ImGui.CalcTextSize(boltStr);
                    var linkGsz = ImGui.CalcTextSize(linkStr);
                    var rtgGsz  = ImGui.CalcTextSize(rtgStr);
                    var boltW   = boltGsz.X + 2f * pad;
                    var linkW   = linkGsz.X + 2f * pad;
                    var rtgW    = rtgGsz.X + 2f * pad;

                    var hasBolt  = node.IsOgcd;
                    var hasLink  = node.GroupId != null;
                    var hasRtg   = node.RetargetMode != 0;
                    var nBadges  = (hasBolt ? 1 : 0) + (hasLink ? 1 : 0) + (hasRtg ? 1 : 0);
                    var totalW   = (hasBolt ? boltW : 0f) + (hasLink ? linkW : 0f) + (hasRtg ? rtgW : 0f)
                                 + (nBadges > 1 ? (nBadges - 1) * gap : 0f);
                    var x        = sp.X + (NodeSize.X - totalW) * 0.5f;

                    void Badge(string glyph, Vector2 gsz, float w, uint bg, float gdx) {
                        var bMin = new Vector2(x, sp.Y + NodeSize.Y - (gsz.Y + 2f * pad) * 0.5f);
                        var bMax = bMin + new Vector2(w, gsz.Y + 2f * pad);
                        dl.AddRectFilled(bMin, bMax, bg, 4f);
                        dl.AddRect(bMin, bMax, darkCol, 4f, ImDrawFlags.None, 1.5f);
                        dl.AddText(bMin + new Vector2(pad + gdx, pad - 1f), darkCol, glyph);
                        x += w + gap;
                    }

                    if (hasBolt) Badge(boltStr, boltGsz, boltW, Style.BadgeOgcdU32(), 0f);
                    if (hasLink) Badge(linkStr, linkGsz, linkW, Style.BadgeComboU32(), 0.5f);
                    if (hasRtg)  Badge(rtgStr,  rtgGsz,  rtgW,  Style.BadgeRetargetU32(), 0f);
                    ImGui.PopFont();
                }

                var label      = node.ActionLabel != "" ? node.ActionLabel : (isTrigger ? "Trigger" : "Action");
                var labelWidth = ImGui.CalcTextSize(label).X;
                var labelPos   = sp + new Vector2((NodeSize.X - labelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, labelPos, label, Col(accentR, accentG, accentB), true);


                // Delete button (top-right, visible on hover)
                if (nodeHovered) {
                    var xBtn = sp + new Vector2(NodeSize.X - 7f, 7f);
                    const float XBr = 7f;
                    const float XS  = 3f;
                    var xHov = Vector2.Distance(mouse2, xBtn) < XBr;
                    dl.AddCircleFilled(xBtn, XBr, xHov ? Col(0.75f, 0.15f, 0.15f) : Col(0.20f, 0.08f, 0.08f, 0.9f));
                    dl.AddCircle(xBtn, XBr, Col(1f, 0.3f, 0.3f, 0.8f), 12, 1.2f);
                    dl.AddLine(xBtn + new Vector2(-XS, -XS), xBtn + new Vector2(XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    dl.AddLine(xBtn + new Vector2(XS, -XS), xBtn + new Vector2(-XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
                    if (xHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        _pendingDeleteNodeId = node.Id;
                }

                // Ports
                if (!isTrigger) {
                    var overInPort = overInStart || (_wireFromNodeId != null && _wireFromNodeId != node.Id
                        && Vector2.Distance(mouse2, inPort) < PortRadius * 3f);
                    dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.55f, 0.80f, 1f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
                }
                var outHover = overOutPort || (_wireToNodeId != null && node.Id != _wireToNodeId
                    && Vector2.Distance(mouse2, outPort) < PortRadius * 3f);   // Mode B source hover
                dl.AddCircleFilled(outPort, PortRadius, outHover ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(outPort, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
            }

            // ── Context menu ──────────────────────────────────────────────
            if (ImGui.BeginPopup($"node_ctx_{node.Id}")) {
                if (isGate) {
                    if (IconMenuItem(FontAwesomeIcon.Filter, "Edit Condition", Style.NodeColU32(node.Type))) OpenConditionEdit(node.Id);
                } else if (isBranch) {
                    if (IconMenuItem(FontAwesomeIcon.List,   "Edit Outputs", Style.NodeColU32(NodeType.Branch))) OpenBranchEdit(node.Id, node.OutputCount);
                } else if (isNote) {
                    if (IconMenuItem(FontAwesomeIcon.Edit,   "Edit Note", Col(0.95f, 0.95f, 0.96f))) OpenNoteEdit(node.Id);
                } else {
                    if (IconMenuItem(FontAwesomeIcon.Edit,   "Edit Action",
                            Style.NodeColU32(node.Type))) OpenPicker(node.Id);
                    if (!isTrigger && IconBeginMenu(FontAwesomeIcon.Crosshairs, "Retarget", Col(0.4f, 0.85f, 1f))) {
                        string[] modes = ["None", "Self", "Lowest HP ally", "Target of target", "Lowest HP enemy", "Dead member"];
                        for (var m = 0; m < modes.Length; m++)
                            if (ImGui.MenuItem(modes[m], "", node.RetargetMode == m)) {
                                node.RetargetMode = m;
                                FlowExecutor.InvalidateFlow(_flow.Id);
                                Commit();
                            }
                        ImGui.EndMenu();
                    }
                }
                ImGui.Separator();
                if (IconMenuItem(FontAwesomeIcon.Copy, "Copy", Col(0.45f, 0.80f, 0.85f))) {
                    _clipboardNodes = [(node.Id, node.Clone(), 0f, 0f)];
                    _clipboardEdges = [];
                }
                if (IconMenuItem(FontAwesomeIcon.TrashAlt, "Delete Node", Col(1f, 0.40f, 0.40f)))  _pendingDeleteNodeId = node.Id;
                if (IconMenuItem(FontAwesomeIcon.Unlink,   "Remove Links", Col(0.95f, 0.60f, 0.30f))) {
                    _flow.Edges.RemoveAll(e => e.FromNodeId == node.Id || e.ToNodeId == node.Id);
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    Commit();
                }
                if (node.GroupId != null && IconMenuItem(FontAwesomeIcon.ObjectUngroup, "Ungroup", Col(1f, 0.70f, 0.20f))) {
                    node.GroupId = null;
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    Commit();
                }
                ImGui.EndPopup();
            }
        }

        // ── Multi-selection context menu ──────────────────────────────────
        if (ImGui.BeginPopup("##multi_node_ctx")) {
            var selCount = _selectedNodeIds.Count;
            // Grouping only applies to Action nodes and needs at least two to be meaningful.
            var actionCount = _flow.Nodes.Count(n => _selectedNodeIds.Contains(n.Id) && n.Type == NodeType.Action);
            if (actionCount >= 2) {
                if (IconMenuItem(FontAwesomeIcon.ObjectGroup, "Group as Combo", Style.ComboU32())) {
                    var gid = Guid.NewGuid().ToString();
                    foreach (var n in _flow.Nodes)
                        if (_selectedNodeIds.Contains(n.Id) && n.Type == NodeType.Action) n.GroupId = gid;
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    Commit();
                    ImGui.CloseCurrentPopup();
                }
                if (IconMenuItem(FontAwesomeIcon.ObjectUngroup, "Ungroup", Col(1f, 0.70f, 0.20f))) {
                    foreach (var n in _flow.Nodes)
                        if (_selectedNodeIds.Contains(n.Id)) n.GroupId = null;
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    Commit();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.Separator();
            }
            if (IconMenuItem(FontAwesomeIcon.TrashAlt, $"Delete {selCount} nodes", Col(1f, 0.40f, 0.40f))) {
                _pendingDeleteMulti = _selectedNodeIds.ToList();   // routed through the confirm choke
                ImGui.CloseCurrentPopup();
            }
            if (IconMenuItem(FontAwesomeIcon.Copy, "Copy", Col(0.45f, 0.80f, 0.85f))) {
                var cx = 0f; var cy = 0f; var cnt = 0;
                foreach (var n in _flow.Nodes) {
                    if (!_selectedNodeIds.Contains(n.Id)) continue;
                    cx += n.X + NodeSize.X * 0.5f;
                    cy += n.Y + NodeHeight(n) * 0.5f;
                    cnt++;
                }
                if (cnt > 0) { cx /= cnt; cy /= cnt; }
                _clipboardNodes = new();
                _clipboardEdges = new();
                foreach (var n in _flow.Nodes) {
                    if (!_selectedNodeIds.Contains(n.Id)) continue;
                    _clipboardNodes.Add((n.Id, n.Clone(), n.X - cx, n.Y - cy));
                }
                foreach (var e in _flow.Edges) {
                    if (_selectedNodeIds.Contains(e.FromNodeId) && _selectedNodeIds.Contains(e.ToNodeId))
                        _clipboardEdges.Add((e.FromNodeId, e.ToNodeId, e.FromPortIndex));
                }
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        // ── Marquee rect ──────────────────────────────────────────────────
        if (_isMarqueeSelecting) {
            var rMin = Vector2.Min(_marqueeStart, _marqueeEnd);
            var rMax = Vector2.Max(_marqueeStart, _marqueeEnd);
            dl.AddRectFilled(rMin, rMax, Style.AccentU32(0.2f), 6f);
            dl.AddRect(rMin, rMax, Style.AccentU32(0.9f), 6f, ImDrawFlags.None, 1.5f);
        }

        // ── Pending deletes (outside node loop) ───────────────────────────
        if (_pendingDeleteNodeId != null) {
            if (_config.ConfirmNodeDelete) {
                _confirmDeleteNodeId = _pendingDeleteNodeId;
                ImGui.OpenPopup("Delete node?##excDelNode");
            } else {
                DeleteNode(_pendingDeleteNodeId);
            }
            _pendingDeleteNodeId = null;
        }
        if (_pendingDeleteMulti != null) {
            if (_config.ConfirmNodeDelete) {
                _confirmDeleteMulti = _pendingDeleteMulti;
                ImGui.OpenPopup("Delete nodes?##excDelMulti");
            } else {
                DeleteSelected(_pendingDeleteMulti);
            }
            _pendingDeleteMulti = null;
        }
        DrawConfirmDeletePopup();
        DrawConfirmDeleteMultiPopup();

        // ── Undo/redo toolbar (before the canvas button so it wins HoveredId over it) ──
        DrawUndoToolbar(canvasMin);

        // ── Canvas input (submitted after nodes so nodes win HoveredId) ───
        ImGui.SetCursorScreenPos(canvasMin);
        ImGui.InvisibleButton("##canvas", canvasSize,
            ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonLeft);
        var canvasActive  = ImGui.IsItemActive();
        var canvasHovered = ImGui.IsItemHovered();

        // Click empty canvas = deselect
        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _wireFromNodeId == null) {
            if (!ImGui.GetIO().KeyCtrl) _selectedNodeIds.Clear();
        }

        // Marquee start
        if (canvasActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left)
            && !_isMarqueeSelecting && _wireFromNodeId == null && _wireToNodeId == null) {
            _isMarqueeSelecting = true;
            _marqueeStart = mouse2 - ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
        }
        if (_isMarqueeSelecting) _marqueeEnd = mouse2;

        // Marquee end
        if (_isMarqueeSelecting && ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
            _isMarqueeSelecting = false;
            var rMin = Vector2.Min(_marqueeStart, _marqueeEnd);
            var rMax = Vector2.Max(_marqueeStart, _marqueeEnd);
            if (!ImGui.GetIO().KeyCtrl) _selectedNodeIds.Clear();
            foreach (var n in _flow.Nodes) {
                var nsp  = canvasMin + _canvasOffset + new Vector2(n.X, n.Y);
                var nMax = nsp + new Vector2(NodeWidthOf(n), NodeHeight(n));
                if (nsp.X < rMax.X && nMax.X > rMin.X && nsp.Y < rMax.Y && nMax.Y > rMin.Y)
                    _selectedNodeIds.Add(n.Id);
            }
        }

        if (!anyNodeRightClicked && canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            _contextMenuCanvasPos = mouse2 - canvasMin - _canvasOffset;
            _dropSrcNodeId = null; _dropDstNodeId = null;   // plain right-click: no dangling wire
            ImGui.OpenPopup("##canvas_ctx");
        }
        var ctxOpen = ImGui.BeginPopup("##canvas_ctx");
        if (!ctxOpen) { _dropSrcNodeId = null; _dropDstNodeId = null; }   // menu dismissed → drop the dangling wire
        if (ctxOpen) {
            if (IconMenuItem(FontAwesomeIcon.Bolt,        "Add Trigger",   Style.NodeColU32(NodeType.Trigger))) AddNode(NodeType.Trigger);
            if (IconMenuItem(FontAwesomeIcon.Magic,       "Add Action",    Style.NodeColU32(NodeType.Action))) AddNode(NodeType.Action);
            if (IconMenuItem(FontAwesomeIcon.CodeBranch,  "Add Priority",  Style.NodeColU32(NodeType.Branch))) AddNode(NodeType.Branch);
            var condCol = Style.NodeColU32(NodeType.StatusCondition);
            if (IconBeginMenu(FontAwesomeIcon.Filter, "Add Condition", condCol)) {
                if (IconMenuItem(FontAwesomeIcon.Filter,    "Job Gauge",  condCol)) AddGateNode(NodeType.Condition);
                if (IconMenuItem(FontAwesomeIcon.Magic,     "Status",     condCol)) AddGateNode(NodeType.StatusCondition);
                if (IconMenuItem(FontAwesomeIcon.Hourglass, "Cooldown",   condCol)) AddGateNode(NodeType.CooldownCondition);
                if (IconMenuItem(FontAwesomeIcon.Crosshairs,"Target",     condCol)) AddGateNode(NodeType.TargetCondition);
                if (IconMenuItem(FontAwesomeIcon.User,      "Player",     condCol)) AddGateNode(NodeType.PlayerCondition);
                if (IconMenuItem(FontAwesomeIcon.Users,     "Party",      condCol)) AddGateNode(NodeType.PartyCondition);
                ImGui.EndMenu();
            }
            if (IconMenuItem(FontAwesomeIcon.StickyNote,  "Add Note",      Style.NodeColU32(NodeType.Note))) AddNoteNode();
            if (_clipboardNodes != null) {
                ImGui.Separator();
                if (IconMenuItem(FontAwesomeIcon.Paste, $"Paste ({_clipboardNodes.Count} nodes)", Col(0.55f, 0.85f, 0.50f))) {
                    var pastePos  = _contextMenuCanvasPos;
                    var idMap     = new Dictionary<string, string>();
                    var groupMap  = new Dictionary<string, string>();
                    var newNodes  = new List<FlowNode>();
                    foreach (var (origId, tmpl, relX, relY) in _clipboardNodes) {
                        var nn = tmpl.Clone();
                        nn.Id = Guid.NewGuid().ToString();
                        nn.X  = MathF.Round((pastePos.X + relX) / GridStep) * GridStep;
                        nn.Y  = MathF.Round((pastePos.Y + relY) / GridStep) * GridStep;
                        // Remap group ids so pasted nodes form their own group, not rejoin the original.
                        if (nn.GroupId is { } gid) {
                            if (!groupMap.TryGetValue(gid, out var ng)) { ng = Guid.NewGuid().ToString(); groupMap[gid] = ng; }
                            nn.GroupId = ng;
                        }
                        idMap[origId] = nn.Id;
                        newNodes.Add(nn);
                    }
                    foreach (var (fromOrig, toOrig, portIdx) in _clipboardEdges!) {
                        if (idMap.TryGetValue(fromOrig, out var fromNew) && idMap.TryGetValue(toOrig, out var toNew))
                            _flow.Edges.Add(new FlowEdge { FromNodeId = fromNew, ToNodeId = toNew, FromPortIndex = portIdx });
                    }
                    _selectedNodeIds.Clear();
                    foreach (var nn in newNodes) {
                        _flow.Nodes.Add(nn);
                        _selectedNodeIds.Add(nn.Id);
                    }
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    Commit();
                }
            }
            ImGui.EndPopup();
        }

        // ── Hint ──────────────────────────────────────────────────────────
        if (_flow.Nodes.Count == 0)
            dl.AddText(canvasMin + new Vector2(12f, 12f), Col(0.333f, 0.353f, 0.388f),
                "Right-click to add nodes  •  Middle-drag to pan  •  Drag output port (right circle) to wire");

        // ── Deferred popup opens (must be outside all BeginPopup contexts) ──
        if (_pendingOpenPicker)     { ImGui.OpenPopup("Pick Action##picker");        _pendingOpenPicker     = false; }
        if (_pendingOpenBranchEdit) { ImGui.OpenPopup("Priority Outputs##branchedit"); _pendingOpenBranchEdit = false; }
        if (_pendingOpenCondEdit)   { ImGui.OpenPopup("Edit Condition##condedit");   _pendingOpenCondEdit   = false; }
        if (_pendingOpenNoteEdit)   { ImGui.OpenPopup("Edit Note##noteedit");        _pendingOpenNoteEdit   = false; }

        // ── Modals ────────────────────────────────────────────────────────
        DrawActionPicker();
        DrawBranchEdit();
        DrawConditionEdit();
        DrawNoteEdit();

        ImGui.PopStyleVar();   // WindowPadding pushed at the top of Draw
    }

    private void AddNode(NodeType type) {
        var node = new FlowNode {
            Type = type,
            X    = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y    = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        TryConnectDropped(node);
        Commit();
        if (type != NodeType.Branch && type != NodeType.Condition)
            OpenPicker(node.Id);
    }

    private void AddGateNode(NodeType type) {
        var node = new FlowNode {
            Type        = type,
            OutputCount = 2,
            X           = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y           = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        TryConnectDropped(node);
        Commit();
        OpenConditionEdit(node.Id);
    }

    // Wire a node just created from the drop-menu into the dangling connection, if any.
    private void TryConnectDropped(FlowNode nn) {
        if (_dropSrcNodeId != null) {
            if (nn.Type != NodeType.Trigger && nn.Type != NodeType.Note) {
                _flow!.Edges.RemoveAll(e => e.FromNodeId == _dropSrcNodeId && e.FromPortIndex == _dropSrcPort);
                _flow.Edges.Add(new FlowEdge { FromNodeId = _dropSrcNodeId, ToNodeId = nn.Id, FromPortIndex = _dropSrcPort });
                FlowExecutor.InvalidateFlow(_flow.Id);
            }
            _dropSrcNodeId = null;
        }
        if (_dropDstNodeId != null) {
            if (nn.Type != NodeType.Note && nn.Id != _dropDstNodeId) {
                _flow!.Edges.RemoveAll(e => e.FromNodeId == nn.Id && e.FromPortIndex == 0);
                _flow.Edges.Add(new FlowEdge { FromNodeId = nn.Id, ToNodeId = _dropDstNodeId, FromPortIndex = 0 });
                FlowExecutor.InvalidateFlow(_flow.Id);
            }
            _dropDstNodeId = null;
        }
    }

    // Short category label used when a gate node has no check selected yet.
    private static string GateCategory(NodeType t) => t switch {
        NodeType.Condition         => "Job Condition",
        NodeType.StatusCondition   => "Status",
        NodeType.CooldownCondition => "Cooldown",
        NodeType.TargetCondition   => "Target",
        NodeType.PlayerCondition   => "Player",
        NodeType.PartyCondition    => "Party",
        _ => "Condition",
    };

    private static string GateNodeLabel(FlowNode n) {
        var op  = ((CompareOp)n.ConditionCompareOp).ToLabel();
        if (n.Type == NodeType.Condition)
            return n.ConditionField != "" ? $"{n.ConditionField} {op} {n.ConditionCompareVal}" : "Job Condition";
        var def = ConditionCatalog.Find(n.Type, n.CheckField);
        if (def == null) return GateCategory(n.Type);
        if (def.Bool) return n.ConditionCompareVal >= 1f ? def.Label : $"!{def.Label}";
        return $"{def.Label} {op} {n.ConditionCompareVal:0.##}";
    }

    private void AddNoteNode() {
        var node = new FlowNode {
            Type = NodeType.Note,
            X    = MathF.Round((_contextMenuCanvasPos.X - 160f * 0.5f) / GridStep) * GridStep,
            Y    = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        TryConnectDropped(node);
        Commit();
        OpenNoteEdit(node.Id);
    }

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

    private void OpenPicker(string nodeId) {
        _pickerNodeId     = nodeId;
        _pickerSearch     = "";
        _pickerLastSearch = "\0";
        _pickerResults.Clear();
        BuildJobCategorySet();
        _pendingOpenPicker = true;
    }

    private void DrawActionPicker() {
        if (_pickerNodeId == null) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(380, 420), new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Pick Action##picker", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##search", ref _pickerSearch, 256);

        if (_pickerSearch != _pickerLastSearch) {
            _pickerLastSearch = _pickerSearch;
            UpdatePickerResults();
        }

        DrawTabButton("PvE", !_pickerPvpTab, () => _pickerPvpTab = false);
        ImGui.SameLine(0, 6f);
        DrawTabButton("PvP",  _pickerPvpTab, () => _pickerPvpTab = true);

        ImGui.BeginChild("##area", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), false);
        DrawPickerList(_pickerPvpTab);
        ImGui.EndChild();

        void DrawPickerList(bool pvp) {
            ImGui.BeginChild(pvp ? "##rpvp" : "##rpve", Vector2.Zero, true);
            foreach (var (id, name, icon, level, isPvp) in _pickerResults) {
                if (isPvp != pvp) continue;
                var rowStart = ImGui.GetCursorPos();
                bool clicked = ImGui.Selectable($"##s{id}", false, ImGuiSelectableFlags.None, new Vector2(0f, 36f));
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
                    var node = _flow!.Nodes.Find(n => n.Id == _pickerNodeId);
                    if (node != null) {
                        node.ActionId    = id;
                        node.ActionLabel = name;
                        node.IconId      = icon;
                        node.IsOgcd      = ActionHelper.IsOgcd(id);
                        FlowExecutor.InvalidateFlow(_flow.Id);
                        Commit();
                    }
                    _pickerNodeId = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndChild();
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(20f, 5f));
        if (ImGui.Button("Cancel")) {
            _pickerNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.PopStyleVar();
        ImGui.EndPopup();
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
        ImGui.InputText("##cfsearch", ref _condFieldSearch, 64);

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
        var defs = ConditionCatalog.For(_condEditType);

        ImGui.TextDisabled("Check");
        ImGui.SetNextItemWidth(CondW);
        ImGui.InputText("##cfsearch", ref _condFieldSearch, 64);

        ImGui.BeginChild("##cffield", new Vector2(CondW, 120f), true);
        if (defs != null)
            foreach (var d in defs) {
                if (_condFieldSearch.Length > 0
                    && !d.Label.Contains(_condFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable(d.Label, d.Key == _condFieldSelected))
                    _condFieldSelected = d.Key;
            }
        ImGui.EndChild();

        var def = ConditionCatalog.Find(_condEditType, _condFieldSelected);
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
