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

public partial class FlowEditorWindow : Window {
    private readonly Configuration  _config;
    private readonly NodeWikiWindow _wiki;
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
    private int     _wireToPortIndex;     // Mode B target input slot (>= 1 = Logic predicate slot)

    // Wire dropped on empty canvas → add-node menu open; the created node auto-connects.
    private string? _dropSrcNodeId;       // dangling source (connect new node as its target)
    private int     _dropSrcPort;
    private string? _dropDstNodeId;       // dangling target (connect new node as its source)
    private int     _dropDstPort;         // dangling target input slot (Logic predicate slots >= 1)
    private string? _editNodeId;          // node being edited in the merged Action/Retarget modal
    private int     _editActiveTab;       // 0 = Action, 1 = Retarget
    // Staged edits — applied to the node only on OK, discarded on Cancel.
    private uint      _editActionId;
    private string    _editActionLabel = "";
    private uint      _editIconId;
    private bool      _editIsOgcd;
    private List<int> _editRetarget = new();
    private string  _pickerSearch     = "";
    private string  _pickerLastSearch = "\0";
    private readonly List<(uint Id, string Name, uint Icon, byte Level, bool IsPvp)> _pickerResults = new();
    private HashSet<uint>? _pickerJobCategoryIds;
    private HashSet<uint>? _pickerJobExclusiveCategoryIds;
    private bool _pickerPvpTab;
    private string? _branchEditNodeId;
    private int     _branchEditCount;
    private string? _logicEditNodeId;
    private int     _logicEditCount;
    private string  _logicEditExpr = "";
    private string? _keybindEditNodeId;
    private uint    _keybindEditVk;
    private string? _toggleEditNodeId;
    private string  _toggleEditName = "";
    private bool    _toggleEditOn;
    private bool    _toggleEditCopied;
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
    private int      _condSource;
    private string   _condParamSearch = "";
    private readonly List<(uint Id, string Name, uint Icon, byte Level)> _condParamResults = new();
    private string   _condParamLastSearch = "\0";
    private CheckParamKind _condParamLastKind = CheckParamKind.None;

    private string? _noteEditNodeId;
    private string  _noteEditText = "";
    private string? _resizingNoteId;

    // Deferred popup opens — set inside popup contexts, opened outside
    private bool _pendingOpenNodeEdit;
    private bool _pendingOpenBranchEdit;
    private bool _pendingOpenCondEdit;
    private bool _pendingOpenNoteEdit;
    private bool _pendingOpenLogicEdit;
    private bool _pendingOpenKeybindEdit;
    private bool _pendingOpenToggleEdit;

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
    private List<(string OrigId, FlowNode Node, float RelX, float RelY)>?  _clipboardNodes;
    private List<(string FromOrig, string ToOrig, int PortIdx, int ToPortIdx)>? _clipboardEdges;

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

    // Live-inspector edge color: dim grey if the source port isn't the taken branch, otherwise
    // the downstream target node's state (green usable / red on-cd / grey gated / gold queued).
    private static uint InspectEdgeColor(ComboFlow flow, FlowNode fn, FlowNode tn, FlowEdge edge) {
        bool sourceOpen =
            fn.Type == NodeType.Trigger ? FlowExecutor.TriggerActive(flow, fn.Id)
          : FlowNode.IsGate(fn.Type)    ? (FlowExecutor.EvalGate(flow, fn) ? edge.FromPortIndex == 0 : edge.FromPortIndex == 1)
          : fn.Type == NodeType.Branch  ? FlowExecutor.ActiveBranchPort(flow, fn.Id) == edge.FromPortIndex
          : true; // action chain step
        if (!sourceOpen) return Col(0.45f, 0.45f, 0.45f, 0.5f);

        if (tn.Type == NodeType.Action) {
            if (FlowExecutor.IsQueuedAction(flow, tn.Id)) {
                var pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 4f);
                return Col(1f, 0.85f, 0.25f, pulse);                 // queued next → gold pulse
            }
            var reachable = FlowExecutor.LiveReachable(flow, tn);
            var ready     = Helpers.CooldownHelper.Ready(tn.ActionId);
            return !reachable ? Col(0.45f, 0.45f, 0.45f, 0.55f)      // gated off → grey
                 : ready      ? Col(0.30f, 0.85f, 0.30f, 0.9f)       // usable → green
                              : Col(0.85f, 0.30f, 0.30f, 0.9f);      // on cd → red
        }
        if (FlowNode.IsGate(tn.Type))
            return FlowExecutor.EvalGate(flow, tn) ? Col(0.30f, 0.85f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f);
        if (tn.Type == NodeType.Branch)
            return FlowExecutor.ActiveBranchPort(flow, tn.Id) >= 0 ? Col(0.30f, 0.85f, 0.30f, 0.9f) : Col(0.45f, 0.45f, 0.45f, 0.55f);

        return Col(0.4f, 0.6f, 1f, 0.85f); // fallback (e.g. Note) — static blue
    }

    // Midpoint of a wire for the delete button (t=0.5 on the Bézier, or the segment centre if straight).
    private static Vector2 WireMidpoint(Vector2 a, Vector2 b) {
        if ((Plugin.Config?.WireStyle ?? WireStyle.Curved) == WireStyle.Straight)
            return (a + b) * 0.5f;
        var cp1 = a + new Vector2(60, 0);
        var cp2 = b - new Vector2(60, 0);
        return 0.125f * a + 0.375f * cp1 + 0.375f * cp2 + 0.125f * b;
    }



    // Hover delete button (top-right X circle). Returns true if it was clicked this frame.
    private static bool DrawNodeDeleteButton(ImDrawListPtr dl, Vector2 center, Vector2 mouse) {
        const float XBr = 7f;
        const float XS  = 3f;
        var xHov = Vector2.Distance(mouse, center) < XBr;
        dl.AddCircleFilled(center, XBr, xHov ? Col(0.75f, 0.15f, 0.15f) : Col(0.20f, 0.08f, 0.08f, 0.9f));
        dl.AddCircle(center, XBr, Col(1f, 0.3f, 0.3f, 0.8f), 12, 1.2f);
        dl.AddLine(center + new Vector2(-XS, -XS), center + new Vector2(XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
        dl.AddLine(center + new Vector2(XS, -XS), center + new Vector2(-XS, XS), Col(1f, 1f, 1f, 0.9f), 1.5f);
        return xHov && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private static float NodeHeight(FlowNode n) =>
        n.Type == NodeType.Note
            ? n.NoteH
            : n.Type == NodeType.Branch || FlowNode.IsGate(n.Type)
                ? MathF.Max(NodeSize.Y, BranchSlotH * Math.Max(n.OutputCount,
                      n.PredicateInputs() > 0 ? n.PredicateInputs() + 1 : 0))
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

    // Screen position of a node's input slot. Nodes with predicate inputs (Logic/Latch) stack
    // their slots down the left side: slot 0 = flow input (top, grey ring), slots 1..N = predicate
    // inputs beneath it at BranchSlotH pitch. Every other node type has just slot 0 at the left
    // midpoint.
    private Vector2 InputPortPos(FlowNode n, int slot, Vector2 canvasMin) =>
        n.PredicateInputs() > 0
            ? canvasMin + _canvasOffset + new Vector2(n.X, (slot + 0.5f) * BranchSlotH + n.Y)
            : canvasMin + _canvasOffset + new Vector2(n.X, n.Y + NodeHeight(n) * 0.5f);

    private static int InputSlotCount(FlowNode n) =>
        n.PredicateInputs() > 0 ? n.PredicateInputs() + 1 : 1;

    // The input slot of this node under a screen point, or null (Triggers/Notes have no input).
    // Logic nodes have no visible flow-input port: with bodyAsFlowInput, any point on the body
    // that isn't a predicate slot counts as the flow input (slot 0) — used when dropping a wire.
    private int? FindInputSlotAt(FlowNode n, Vector2 pt, Vector2 canvasMin, float radiusMul = 3f,
            bool bodyAsFlowInput = false) {
        if (n.Type is NodeType.Trigger or NodeType.Note) return null;
        if (n.PredicateInputs() > 0) {
            for (var s = 0; s <= n.PredicateInputs(); s++)
                if (Vector2.Distance(pt, InputPortPos(n, s, canvasMin)) < PortRadius * radiusMul)
                    return s;
            if (bodyAsFlowInput) {
                var min = canvasMin + _canvasOffset + new Vector2(n.X, n.Y);
                var max = min + new Vector2(NodeSize.X, NodeHeight(n));
                if (pt.X >= min.X && pt.X <= max.X && pt.Y >= min.Y && pt.Y <= max.Y) return 0;
            }
            return null;
        }
        return Vector2.Distance(pt, InputPortPos(n, 0, canvasMin)) < PortRadius * radiusMul ? 0 : null;
    }

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

    public FlowEditorWindow(Configuration config, NodeWikiWindow wiki) : base("Flow Editor###ExComboEditor") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
        // Panning places node hit-boxes off-canvas; without this the window grows scrollable space.
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        _config = config;
        _wiki   = wiki;
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

        // Live flow inspector: draw per-type state rings/edges whenever the toggle is on and a local
        // player exists (evaluating conditions during login/zoning can crash on unloaded memory).
        var inspect = _config.ShowConditionState && Plugin.ObjectTable.LocalPlayer != null;

        // ── Edges ─────────────────────────────────────────────────────────
        DrawEdges(dl, canvasMin, mouse2, inspect);

        // ── Pending wires (drag-to-connect, both directions) ──────────────
        DrawPendingWires(dl, canvasMin, mouse2);

        // ── Combo group boxes + selection envelope (behind nodes) ─────────
        DrawGroupBoxes(dl, canvasMin);
        DrawSelectionEnvelope(dl, canvasMin);

        // ── Nodes ─────────────────────────────────────────────────────────
        var anyNodeRightClicked = false;

        foreach (var node in _flow.Nodes) {
            var isTrigger   = node.Type == NodeType.Trigger;
            var isBranch    = node.Type == NodeType.Branch;
            var isGate      = FlowNode.IsGate(node.Type);
            var isLogic     = node.Type == NodeType.LogicCondition;
            var isKeybind   = node.Type == NodeType.KeybindCondition;
            var isToggle    = node.Type == NodeType.ToggleCondition;
            var isLatch     = node.Type == NodeType.LatchCondition;
            var isJobCond   = node.Type == NodeType.Condition;
            var isNote      = node.Type == NodeType.Note;
            var nodeH       = NodeHeight(node);
            var nodeW       = NodeWidthOf(node);
            var sp          = canvasMin + _canvasOffset + new Vector2(node.X, node.Y);
            var inPort      = InputPortPos(node, 0, canvasMin);

            // Input slots are grabbable to start a wire the other way (input → output, Mode B).
            // Logic nodes expose extra predicate slots below the flow input.
            var overInSlot  = _wireFromNodeId == null && _wireToNodeId == null
                ? FindInputSlotAt(node, mouse2, canvasMin, 2f) : null;
            var overInStart = overInSlot != null;

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
            if (overInStart && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                _wireToNodeId    = node.Id;
                _wireToPortIndex = overInSlot!.Value;
            }

            if (nodeHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
                if (isBranch)       OpenBranchEdit(node.Id, node.OutputCount);
                else if (isLogic)   OpenLogicEdit(node.Id);
                else if (isKeybind) OpenKeybindEdit(node.Id);
                else if (isToggle)  OpenToggleEdit(node.Id);
                else if (isLatch)   { /* nothing to configure */ }
                else if (isGate)    OpenConditionEdit(node.Id);
                else if (isNote)    OpenNoteEdit(node.Id);
                else                OpenPicker(node.Id);
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
                if (nodeHovered && DrawNodeDeleteButton(dl, sp + new Vector2(nodeW - 7f, 7f), mouse2))
                    _pendingDeleteNodeId = node.Id;

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

                // Live flow inspector: which output port is currently driving the rotation (-1 = none).
                var inspActivePort = inspect ? FlowExecutor.ActiveBranchPort(_flow!, node.Id) : -1;
                if (inspActivePort >= 0)
                    DrawHelpers.DrawDashedRect(dl, sp - new Vector2(5f, 5f), sp + new Vector2(NodeSize.X + 5f, nodeH + 5f),
                        Col(0.30f, 0.85f, 0.30f, 0.6f), 2f, 6f, 4f, 6f);

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

                    // Live flow inspector: ring the active port bright green.
                    if (p == inspActivePort)
                        dl.AddCircle(portPos, PortRadius + 2.5f, Col(0.35f, 1f, 0.35f, 0.95f), 16, 2.5f);

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
                if (nodeHovered && DrawNodeDeleteButton(dl, sp + new Vector2(NodeSize.X - 7f, 7f), mouse2))
                    _pendingDeleteNodeId = node.Id;
            } else if (isGate) {
                // ── Condition-family node draw (all gate types) ───────────
                var condAccent = Style.NodeColU32(node.Type);
                var borderCol  = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? condAccent
                        : Style.NodeColU32(node.Type, 0.5f);
                dl.AddRectFilled(sp, sp + new Vector2(NodeSize.X, nodeH),
                    isLogic || isLatch || isKeybind || isToggle
                        ? Col(0.13f, 0.12f, 0.04f) : Col(0.12f, 0.08f, 0.03f), 6f);

                // Body icon: job gauge (legacy + GaugeCondition) = job icon; Status/Cooldown = picked
                // status/action icon; Target/Player/Party/Action = a category glyph.
                if (isJobCond || node.Type == NodeType.GaugeCondition) {
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
                } else if (node.Type is NodeType.TargetCondition or NodeType.PlayerCondition or NodeType.PartyCondition
                        or NodeType.ActionHistoryCondition or NodeType.LogicCondition
                        or NodeType.KeybindCondition or NodeType.ToggleCondition or NodeType.LatchCondition) {
                    var glyph = node.Type switch {
                        NodeType.TargetCondition => FontAwesomeIcon.Crosshairs,
                        NodeType.PlayerCondition => FontAwesomeIcon.User,
                        NodeType.ActionHistoryCondition => FontAwesomeIcon.History,
                        NodeType.LogicCondition  => FontAwesomeIcon.Microchip,
                        NodeType.KeybindCondition => FontAwesomeIcon.Keyboard,
                        NodeType.ToggleCondition => node.ToggleOn ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff,
                        NodeType.LatchCondition  => FontAwesomeIcon.Lock,
                        _                        => FontAwesomeIcon.Users,
                    };
                    // Toggle glyph reads its state: full node color when ON, dim grey when OFF.
                    var glyphCol = isToggle && !node.ToggleOn
                        ? Col(0.55f, 0.55f, 0.58f, 0.7f)
                        : Style.NodeColU32(node.Type, 0.85f);
                    var gstr = glyph.ToIconString();
                    using (IconFontLarge.Push()) {
                        var font = ImGui.GetFont();
                        var sz   = ImGui.GetFontSize();
                        var gsz  = ImGui.CalcTextSize(gstr);
                        dl.AddText(font, sz, sp + new Vector2((NodeSize.X - gsz.X) * 0.5f, (nodeH - gsz.Y) * 0.5f),
                            glyphCol, gstr);
                    }
                }

                dl.AddRect(sp, sp + new Vector2(NodeSize.X, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                // Live flow inspector: tint an outer ring green/red by current eval.
                if (inspect) {
                    bool pass = FlowExecutor.EvalGate(_flow!, node);
                    var tint  = pass ? Col(0.30f, 0.85f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f);
                    DrawHelpers.DrawDashedRect(dl, sp - new Vector2(5f, 5f), sp + new Vector2(NodeSize.X + 5f, nodeH + 5f),
                        tint, 2f, 6f, 4f, 6f);
                }

                var condLabel      = GateNodeLabel(node);
                // Invalid logic expression → red warning label instead of the orange accent.
                var condLabelCol   = condAccent;
                if (isLogic) {
                    var ast = LogicExpr.Cached(node.LogicExpr is "" ? "1 AND 2" : node.LogicExpr);
                    if (ast == null || ast.MaxInput > node.LogicInputCount) condLabelCol = Col(1f, 0.35f, 0.35f);
                }
                var condLabelWidth = ImGui.CalcTextSize(condLabel).X;
                var condLabelPos   = sp + new Vector2((NodeSize.X - condLabelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, condLabelPos, condLabel, condLabelCol, true);

                // Input slots down the left side: slot 0 = flow input (grey ring, same as other
                // nodes), slots 1..N = numbered predicate inputs (node-colored rings). Flow wires
                // can also be dropped anywhere on the body.
                for (var s = 0; s < InputSlotCount(node); s++) {
                    var slotPos  = InputPortPos(node, s, canvasMin);
                    var overSlot = overInSlot == s || (_wireFromNodeId != null && _wireFromNodeId != node.Id
                        && Vector2.Distance(mouse2, slotPos) < PortRadius * 3f);
                    dl.AddCircleFilled(slotPos, PortRadius, overSlot ? Col(0.35f, 0.65f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    var ringCol = overSlot ? Col(0.55f, 0.80f, 1f)
                                : s == 0   ? Col(0.45f, 0.45f, 0.60f)
                                           : Style.NodeColU32(node.Type, 0.9f);   // predicate slot
                    dl.AddCircle(slotPos, PortRadius, ringCol, 12, 1.5f);
                    if (s >= 1)
                        DrawHelpers.DrawText(dl, slotPos + new Vector2(PortRadius + 4f, -8f),
                            isLatch ? (s == 1 ? "S" : "R") : s.ToString(),
                            Style.NodeColU32(node.Type, 0.8f), false);
                }

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
                if (nodeHovered && DrawNodeDeleteButton(dl, sp + new Vector2(NodeSize.X - 7f, 7f), mouse2))
                    _pendingDeleteNodeId = node.Id;
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

                // Live flow inspector: triggers show an active-state ring; actions show a
                // usable(green)/blocked(red) ring plus a gold pulse on the queued next action.
                if (inspect) {
                    if (isTrigger) {
                        if (FlowExecutor.TriggerActive(_flow!, node.Id))
                            DrawHelpers.DrawDashedRect(dl, sp - new Vector2(5f, 5f), sp + NodeSize + new Vector2(5f, 5f),
                                Col(0.30f, 0.85f, 0.30f, 0.9f), 2f, 6f, 4f, 6f);
                    } else {
                        var reachable = FlowExecutor.LiveReachable(_flow!, node);
                        var ready     = Helpers.CooldownHelper.Ready(node.ActionId);
                        var tint = !reachable ? Col(0.45f, 0.45f, 0.45f, 0.6f)   // grey/dim = gated off (upstream cond false)
                                 : ready      ? Col(0.30f, 0.85f, 0.30f, 0.9f)   // green     = reachable + usable
                                              : Col(0.85f, 0.30f, 0.30f, 0.9f);  // red       = reachable but on CD/blocked
                        if (FlowExecutor.IsQueuedAction(_flow!, node.Id)) {
                            // Queued next → solid gold pulse replaces the dashed status ring.
                            var pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 4f);
                            dl.AddRect(sp - new Vector2(5f, 5f), sp + NodeSize + new Vector2(5f, 5f),
                                Col(1f, 0.85f, 0.25f, pulse), 6f, ImDrawFlags.None, 2f);
                        } else {
                            DrawHelpers.DrawDashedRect(dl, sp - new Vector2(5f, 5f), sp + NodeSize + new Vector2(5f, 5f),
                                tint, 2f, 6f, 4f, 6f);
                        }
                    }
                }

                // Status badges — centered on the bottom edge (half below). oGCD = lightning,
                // combo-group = chain. When a node has both, lay them out as a centered pair.
                if (!isTrigger && (node.IsOgcd || node.GroupId != null || node.RetargetPriority.Count > 0 || node.RetargetMode != 0)) {
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
                    var hasRtg   = node.RetargetPriority.Count > 0 || node.RetargetMode != 0;
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
                    if (hasRtg)  Badge(rtgStr,  rtgGsz,  rtgW,  Style.BadgeRetargetU32(), 1f);
                    ImGui.PopFont();
                }

                var label      = node.ActionLabel != "" ? node.ActionLabel : (isTrigger ? "Trigger" : "Action");
                var labelWidth = ImGui.CalcTextSize(label).X;
                var labelPos   = sp + new Vector2((NodeSize.X - labelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, labelPos, label, Col(accentR, accentG, accentB), true);


                // Delete button (top-right, visible on hover)
                if (nodeHovered && DrawNodeDeleteButton(dl, sp + new Vector2(NodeSize.X - 7f, 7f), mouse2))
                    _pendingDeleteNodeId = node.Id;

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
                if (isLogic) {
                    if (IconMenuItem(FontAwesomeIcon.Microchip, "Edit Logic", Style.NodeColU32(node.Type))) OpenLogicEdit(node.Id);
                } else if (isKeybind) {
                    if (IconMenuItem(FontAwesomeIcon.Keyboard, "Edit Keybind", Style.NodeColU32(node.Type))) OpenKeybindEdit(node.Id);
                } else if (isToggle) {
                    if (IconMenuItem(node.ToggleOn ? FontAwesomeIcon.ToggleOff : FontAwesomeIcon.ToggleOn,
                            node.ToggleOn ? "Switch Off" : "Switch On", Style.NodeColU32(node.Type))) {
                        node.ToggleOn = !node.ToggleOn;
                        FlowExecutor.InvalidateFlow(_flow.Id);
                        Commit();
                    }
                    if (IconMenuItem(FontAwesomeIcon.Edit, "Edit Toggle", Style.NodeColU32(node.Type))) OpenToggleEdit(node.Id);
                    if (node.ActionLabel != "" && IconMenuItem(FontAwesomeIcon.Copy, "Copy Command", Col(0.45f, 0.80f, 0.85f)))
                        Helpers.ClipboardHelper.SetText(ToggleCommand(node.ActionLabel));
                } else if (isLatch) {
                    if (IconMenuItem(FontAwesomeIcon.Unlock, "Reset Latch State", Style.NodeColU32(node.Type)))
                        FlowExecutor.ResetLatch(_flow, node.Id);
                } else if (isGate) {
                    if (IconMenuItem(FontAwesomeIcon.Filter, "Edit Condition", Style.NodeColU32(node.Type))) OpenConditionEdit(node.Id);
                } else if (isBranch) {
                    if (IconMenuItem(FontAwesomeIcon.List,   "Edit Outputs", Style.NodeColU32(NodeType.Branch))) OpenBranchEdit(node.Id, node.OutputCount);
                } else if (isNote) {
                    if (IconMenuItem(FontAwesomeIcon.Edit,   "Edit Note", Col(0.95f, 0.95f, 0.96f))) OpenNoteEdit(node.Id);
                } else {
                    if (IconMenuItem(FontAwesomeIcon.Edit,   "Edit Action",
                            Style.NodeColU32(node.Type))) OpenPicker(node.Id);
                    if (!isTrigger && IconMenuItem(FontAwesomeIcon.Crosshairs, "Retarget Priority", Col(0.4f, 0.85f, 1f)))
                        OpenRetargetEdit(node.Id);
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
                        _clipboardEdges.Add((e.FromNodeId, e.ToNodeId, e.FromPortIndex, e.ToPortIndex));
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
            _dropSrcNodeId = null; _dropDstNodeId = null; _dropDstPort = 0;   // plain right-click: no dangling wire
            ImGui.OpenPopup("##canvas_ctx");
        }
        var ctxOpen = ImGui.BeginPopup("##canvas_ctx");
        if (!ctxOpen) { _dropSrcNodeId = null; _dropDstNodeId = null; _dropDstPort = 0; }   // menu dismissed → drop the dangling wire
        if (ctxOpen) {
            if (IconMenuItem(FontAwesomeIcon.Bolt,        "Add Trigger",   Style.NodeColU32(NodeType.Trigger))) AddNode(NodeType.Trigger);
            if (IconMenuItem(FontAwesomeIcon.Magic,       "Add Action",    Style.NodeColU32(NodeType.Action))) AddNode(NodeType.Action);
            if (IconMenuItem(FontAwesomeIcon.CodeBranch,  "Add Priority",  Style.NodeColU32(NodeType.Branch))) AddNode(NodeType.Branch);
            var condCol = Style.NodeColU32(NodeType.StatusCondition);
            if (IconBeginMenu(FontAwesomeIcon.Filter, "Add Condition", condCol)) {
                if (IconMenuItem(FontAwesomeIcon.ChartBar,  "Gauge",      condCol)) AddGateNode(NodeType.GaugeCondition);
                if (IconMenuItem(FontAwesomeIcon.Magic,     "Status",     condCol)) AddGateNode(NodeType.StatusCondition);
                if (IconMenuItem(FontAwesomeIcon.Hourglass, "Cooldown",   condCol)) AddGateNode(NodeType.CooldownCondition);
                if (IconMenuItem(FontAwesomeIcon.Crosshairs,"Target",     condCol)) AddGateNode(NodeType.TargetCondition);
                if (IconMenuItem(FontAwesomeIcon.User,      "Player",     condCol)) AddGateNode(NodeType.PlayerCondition);
                if (IconMenuItem(FontAwesomeIcon.Users,     "Party",      condCol)) AddGateNode(NodeType.PartyCondition);
                if (IconMenuItem(FontAwesomeIcon.History,   "Action History", condCol)) AddGateNode(NodeType.ActionHistoryCondition);
                ImGui.EndMenu();
            }
            var logicCol = Style.NodeColU32(NodeType.LogicCondition);
            if (IconBeginMenu(FontAwesomeIcon.Microchip, "Add Logic", logicCol)) {
                if (IconMenuItem(FontAwesomeIcon.Microchip, "Expression", logicCol)) AddLogicNode();
                if (IconMenuItem(FontAwesomeIcon.Lock,      "Latch",      logicCol)) AddLatchNode();
                if (IconMenuItem(FontAwesomeIcon.Keyboard,  "Keybind",    logicCol)) AddGateNode(NodeType.KeybindCondition);
                if (IconMenuItem(FontAwesomeIcon.ToggleOn,  "Toggle",     logicCol)) AddGateNode(NodeType.ToggleCondition);
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
                    foreach (var (fromOrig, toOrig, portIdx, toPortIdx) in _clipboardEdges!) {
                        if (idMap.TryGetValue(fromOrig, out var fromNew) && idMap.TryGetValue(toOrig, out var toNew))
                            _flow.Edges.Add(new FlowEdge { FromNodeId = fromNew, ToNodeId = toNew, FromPortIndex = portIdx, ToPortIndex = toPortIdx });
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
        if (_pendingOpenNodeEdit)   { ImGui.OpenPopup("Edit Action##nodeedit");      _pendingOpenNodeEdit   = false; }
        if (_pendingOpenBranchEdit) { ImGui.OpenPopup("Priority Outputs##branchedit"); _pendingOpenBranchEdit = false; }
        if (_pendingOpenCondEdit)   { ImGui.OpenPopup("Edit Condition##condedit");   _pendingOpenCondEdit   = false; }
        if (_pendingOpenNoteEdit)   { ImGui.OpenPopup("Edit Note##noteedit");        _pendingOpenNoteEdit   = false; }
        if (_pendingOpenLogicEdit)   { ImGui.OpenPopup("Edit Logic##logicedit");     _pendingOpenLogicEdit   = false; }
        if (_pendingOpenKeybindEdit) { ImGui.OpenPopup("Edit Keybind##keybindedit"); _pendingOpenKeybindEdit = false; }
        if (_pendingOpenToggleEdit)  { ImGui.OpenPopup("Edit Toggle##toggleedit");   _pendingOpenToggleEdit  = false; }

        // ── Modals ────────────────────────────────────────────────────────
        DrawNodeEdit();
        DrawBranchEdit();
        DrawConditionEdit();
        DrawNoteEdit();
        DrawLogicEdit();
        DrawKeybindEdit();
        DrawToggleEdit();

        ImGui.PopStyleVar();   // WindowPadding pushed at the top of Draw
    }


}
