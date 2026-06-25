using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ExCombo.Flow;
using ExCombo.Helpers;
using ExCombo.Helpers.DataSources;
using LuminaAction   = Lumina.Excel.Sheets.Action;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaStatus   = Lumina.Excel.Sheets.Status;

namespace ExCombo.Windows;

public class FlowEditorWindow : Window {
    private const float NodeWidth  = 88f;
    private const float NodeHeight = 84f;
    private const float PortRadius = 6f;
    private const float PortHitR   = 12f;
    private const float NodeBorder = 2.5f;
    private const float NodeRound  = 6f;

    private static uint Bg1     => Col(0.102f, 0.106f, 0.118f);
    private static uint Bg2     => Col(0.145f, 0.149f, 0.169f);
    private static uint Bg3     => Col(0.173f, 0.180f, 0.200f);
    private static uint Text1   => Col(0.827f, 0.831f, 0.839f);
    private static uint Text2   => Col(0.565f, 0.573f, 0.588f);
    private static uint Accent  => Col(0.455f, 0.765f, 1.000f);
    private static uint BorderB => Col(0.333f, 0.353f, 0.388f);
    private static uint Err     => Col(1.000f, 0.522f, 0.569f);
    private static uint Success => Col(0.635f, 0.855f, 0.549f);
    private static uint Purple  => Col(0.600f, 0.400f, 0.800f);
    private static uint Orange  => Col(1.000f, 0.600f, 0.100f);
    private static uint TrueCol => Col(0.400f, 0.900f, 0.400f);
    private static uint FalseC  => Col(1.000f, 0.380f, 0.380f);

    private readonly Configuration _config;
    private readonly IDataManager  _dataManager;

    private ComboFlow? _flow;
    public string? ActiveFlowId => _flow?.Id;

    private Vector2   _canvasOffset = Vector2.Zero;
    private FlowNode? _draggingNode;
    private string?   _pendingFrom;
    private bool?     _pendingFromBranch;
    private string?   _autoConnectFrom;
    private bool?     _autoConnectBranch;
    private string?   _pendingDeleteNodeId;
    private string?   _pendingDeleteEdgeId;

    private bool    _openCanvasCtx;
    private Vector2 _contextMenuCanvasPos;

    private bool _animEnabled;
    private bool _openAddTrigger;
    private bool _openAddAction;
    private bool  _openAddGroup;
    private bool  _openAddCondition;
    private bool  _openAddJobCondition;
    private bool _openAddWeaveCondition;
    private int  _weaveMode; // 0=Any, 1=Early, 2=Late
    private bool      _openEditCondition;
    private FlowNode? _editConditionNode;
    private int  _addGroupPriority = 1;

    private HashSet<string> _selectedNodeIds      = new();
    private bool            _isRectSelecting;
    private bool            _pendingClearToSingle;
    private bool            _pendingDeleteSelection;
    private bool            _nodePopupOpen;
    private bool            _pendingPaste;
    private Vector2?        _pendingPastePos;
    private Vector2         _rectSelectStart;
    private Vector2         _rectSelectEnd;
    private List<FlowNode>? _clipboard;
    private List<FlowEdge>? _clipboardEdges;

    // Action picker shared between Trigger/Action/Condition popups
    private readonly List<(uint Id, string Name, byte Level, uint IconId)> _pickerCache = [];
    private string _pickerSearch = "";
    private (uint id, string name, uint iconId)? _pickerSelected;
    private bool _pickerNeedsRebuild = true;

    // Condition popup state
    private int    _condKind;
    private int    _condStatusSrc;   // 0=Player,1=Target
    private int    _condCompareOp = 4;
    private float  _condCompareVal;

    // Status picker (HasStatus condition)
    private readonly List<(uint Id, string Name, uint IconId)> _statusPickerCache = [];
    private string _statusPickerSearch = "";
    private (uint id, string name, uint iconId)? _statusPickerSelected;
    private bool _statusPickerNeedsRebuild = true;

    // Job resource picker (JobResource condition)
    private readonly List<(int Index, string Name, float MaxVal)> _resourceFieldCache = [];
    private string _resourceFieldSearch    = "";
    private int    _resourceFieldSelected  = -1;
    private bool   _resourceFieldNeedsRebuild = true;

    private static readonly string[] CondKindLabels = {
        "Last Action", "Has Status", "CD Ready", "In Combat", "Weapon Drawn", "Usable", "Highlighted", "In Range", "In LoS"
    };
    private static readonly string[] CompareOpLabels = { "==", "!=", "<", ">", "<=", ">=" };

    public FlowEditorWindow(Configuration config, IDataManager dataManager)
        : base("Flow Editor###ExComboEditor") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(9999, 9999),
        };
        _config      = config;
        _dataManager = dataManager;
    }

    public void SetFlow(ComboFlow flow) {
        if (_flow?.Id != flow.Id) {
            _canvasOffset    = new Vector2(20f, 20f);
            _selectedNodeIds.Clear();
            _isRectSelecting = false;
        }
        _flow = flow;
        WindowName = $"Flow Editor — {flow.Name}###ExComboEditor";
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        if (_flow == null) { ImGui.TextDisabled("No flow open. Select a flow in the main window."); return; }
        DrawToolbar();
        ImGui.Separator();
        DrawCanvas();
        DrawPopups();
    }

    // ──────────────────────────── Toolbar ────────────────────────────

    private void DrawToolbar() {
        ImGui.TextDisabled("Right-click canvas to add nodes  •  Drag port to connect  •  Right-click node to delete  •  Alt+drag / MMB to pan");
        ImGui.SameLine(ImGui.GetWindowWidth() - 260f);
        ImGui.Checkbox("Show Execution##anim", ref _animEnabled);
        FlowExecutor.TraceEnabled = _animEnabled;
        ImGui.SameLine(ImGui.GetWindowWidth() - 90f);
        if (ImGui.SmallButton("Reset View")) _canvasOffset = new Vector2(20f, 20f);
    }

    // ──────────────────────────── Canvas ────────────────────────────

    private void DrawCanvas() {
        var canvasOrigin = ImGui.GetCursorScreenPos();
        var canvasSize   = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 10f || canvasSize.Y < 10f) return;

        ImGui.InvisibleButton("##canvas_bg", canvasSize);

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(canvasOrigin, canvasOrigin + canvasSize, true);

        dl.AddRectFilled(canvasOrigin, canvasOrigin + canvasSize, Bg1);
        DrawGrid(dl, canvasOrigin, canvasSize);

        foreach (var edge in _flow!.Edges) DrawEdge(dl, canvasOrigin, edge);
        if (_pendingFrom != null)           DrawPendingEdge(dl, canvasOrigin);
        _nodePopupOpen = false;
        foreach (var node in _flow.Nodes)  DrawNode(dl, canvasOrigin, node);
        DrawSelectionOverlay(dl, canvasOrigin);

        HandleInput(canvasOrigin, canvasSize);

        dl.PopClipRect();

        if (_pendingDeleteNodeId != null) {
            _flow.Nodes.RemoveAll(n => n.Id == _pendingDeleteNodeId);
            _flow.Edges.RemoveAll(e => e.FromNodeId == _pendingDeleteNodeId || e.ToNodeId == _pendingDeleteNodeId);
            _pendingDeleteNodeId = null;
            _config.Save();
        }
        if (_pendingDeleteEdgeId != null) {
            _flow.Edges.RemoveAll(e => e.Id == _pendingDeleteEdgeId);
            _pendingDeleteEdgeId = null;
            _config.Save();
        }
        if (_pendingDeleteSelection) {
            foreach (var id in _selectedNodeIds) {
                _flow.Nodes.RemoveAll(n => n.Id == id);
                _flow.Edges.RemoveAll(e => e.FromNodeId == id || e.ToNodeId == id);
            }
            _selectedNodeIds.Clear();
            _pendingDeleteSelection = false;
            _config.Save();
        }
        if (_pendingPaste) {
            PasteClipboard(_pendingPastePos);
            _pendingPaste    = false;
            _pendingPastePos = null;
        }
    }

    // ──────────────────────────── Grid ────────────────────────────

    private void DrawGrid(ImDrawListPtr dl, Vector2 origin, Vector2 size) {
        const float step = 40f;
        var col = Col(0.216f, 0.227f, 0.251f, 0.6f);
        var ox  = ((_canvasOffset.X % step) + step) % step;
        var oy  = ((_canvasOffset.Y % step) + step) % step;
        for (var x = ox; x < size.X; x += step)
            dl.AddLine(origin + new Vector2(x, 0), origin + new Vector2(x, size.Y), col);
        for (var y = oy; y < size.Y; y += step)
            dl.AddLine(origin + new Vector2(0, y), origin + new Vector2(size.X, y), col);
    }

    // ──────────────────────────── Node ────────────────────────────

    private static Vector2 NodeSize(FlowNode _) => new(NodeWidth, NodeHeight);

    private void DrawNode(ImDrawListPtr dl, Vector2 origin, FlowNode node) {
        var p      = NodeScreenPos(origin, node);
        var nodeSz = NodeSize(node);

        uint borderCol = _selectedNodeIds.Contains(node.Id)
            ? Col(1f, 1f, 1f, 1f)
            : node.Type switch {
                NodeType.Trigger   => Success,
                NodeType.Action    => Accent,
                NodeType.Group     => Purple,
                NodeType.Condition => Orange,
                _                  => BorderB,
            };

        // Execution glow
        if (_animEnabled && FlowExecutor.NodeTrace.TryGetValue(node.Id, out var trace)) {
            var now     = Environment.TickCount64;
            var age     = (now - trace.Tick)        / 1000f;
            var settled = (now - trace.StreakStart) / 1000f;
            if (age < 0.1f && settled >= 0.2f) {
                var t     = Environment.TickCount64 / 1000f;
                var pulse = 0.55f + 0.45f * MathF.Abs(MathF.Sin(t * MathF.PI * 2.8f));
                var alpha = pulse;
                var ex    = 8f + pulse * 4f;
                Vector4 gv;
                if (node.Type == NodeType.Condition)
                    gv = trace.Eval == true
                        ? new Vector4(0.400f, 0.900f, 0.400f, alpha)
                        : new Vector4(1.000f, 0.380f, 0.380f, alpha);
                else
                    gv = trace.Eval == true
                        ? new Vector4(0.455f, 0.765f, 1.000f, alpha)
                        : new Vector4(0.827f, 0.831f, 0.839f, alpha);
                dl.AddRect(p - new Vector2(ex),   p + new Vector2(nodeSz.X + ex, nodeSz.Y + ex),
                    ImGui.ColorConvertFloat4ToU32(gv with { W = gv.W * 0.30f }),
                    NodeRound + ex * 0.5f, ImDrawFlags.None, ex * 0.6f);
                dl.AddRect(p - new Vector2(4f),   p + new Vector2(nodeSz.X + 4f, nodeSz.Y + 4f),
                    ImGui.ColorConvertFloat4ToU32(gv with { W = gv.W * 0.65f }),
                    NodeRound + 2f, ImDrawFlags.None, 3f);
                dl.AddRect(p - new Vector2(1.5f), p + new Vector2(nodeSz.X + 1.5f, nodeSz.Y + 1.5f),
                    ImGui.ColorConvertFloat4ToU32(gv),
                    NodeRound + 1f, ImDrawFlags.None, 2f);
            }
        }

        // Shadow
        dl.AddRectFilled(p + new Vector2(3, 5), p + new Vector2(nodeSz.X + 3, nodeSz.Y + 5),
            Col(0f, 0f, 0f, 0.5f), NodeRound);

        // Background
        if (node.Type == NodeType.Group) {
            dl.AddRectFilled(p, p + nodeSz, Col(0.118f, 0.082f, 0.200f), NodeRound);
        } else if (node.Type == NodeType.Condition) {
            dl.AddRectFilled(p, p + nodeSz, Col(0.120f, 0.085f, 0.020f), NodeRound);
            DrawConditionNodeBody(dl, p, node);
        } else {
            dl.AddRectFilled(p, p + nodeSz, Bg2, NodeRound);
            var iconWrap = GetIconWrap(node.IconId);
            if (iconWrap != null)
                dl.AddImageRounded(iconWrap.Handle, p, p + nodeSz,
                    Vector2.Zero, Vector2.One, 0xFFFFFFFF, NodeRound, ImDrawFlags.RoundCornersAll);
            else
                dl.AddRectFilled(p, p + nodeSz, Bg3, NodeRound);
        }

        if (node.Type == NodeType.Group) {
            var numStr = node.Priority.ToString();
            var numSz  = ImGui.CalcTextSize(numStr);
            dl.AddText(ImGui.GetFont(), 32f,
                new Vector2(p.X + (NodeWidth - numSz.X * 2f) * 0.5f, p.Y + (NodeHeight - 32f) * 0.5f),
                Col(0.769f, 0.690f, 0.961f), numStr);
        }

        dl.AddRect(p, p + nodeSz, borderCol, NodeRound, ImDrawFlags.None, NodeBorder);

        DrawPorts(dl, origin, node);
        DrawNodeLabel(dl, p, node);

        // Right-click → node context menu
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"##hdr_{node.Id}", new Vector2(NodeWidth - PortRadius * 2, NodeHeight));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
            if (!_selectedNodeIds.Contains(node.Id))
                _selectedNodeIds = new HashSet<string> { node.Id };
            ImGui.OpenPopup($"##nctx_{node.Id}");
        }

        var typeLabel = node.Type switch {
            NodeType.Trigger   => "TRIGGER",
            NodeType.Action    => "ACTION",
            NodeType.Group     => "PRIORITY GROUP",
            NodeType.Condition => "CONDITION",
            _                  => "?",
        };

        if (ImGui.BeginPopup($"##nctx_{node.Id}")) {
            _nodePopupOpen = true;
            ImGui.TextDisabled(typeLabel);
            if (node.Type == NodeType.Group) {
                ImGui.Separator();
                ImGui.Text("Priority:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(60f);
                var prio = node.Priority;
                if (ImGui.InputInt($"##grpprio_{node.Id}", ref prio)) {
                    node.Priority = Math.Max(1, prio);
                    _config.Save();
                }
            }
            if (node.Type == NodeType.Condition && !(_selectedNodeIds.Contains(node.Id) && _selectedNodeIds.Count > 1)) {
                ImGui.Separator();
                if (ImGui.MenuItem("Edit...")) {
                    _editConditionNode  = node;
                    _condCompareOp      = node.CompareOp;
                    _condCompareVal     = node.CompareVal;
                    _condStatusSrc      = (int)node.ConditionSource;

                    bool needsAction = node.ConditionKind is ConditionKind.LastAction or ConditionKind.CooldownReady
                                                         or ConditionKind.ActionUsable or ConditionKind.ActionHighlighted
                                                         or ConditionKind.ActionInRange or ConditionKind.TargetInLoS;
                    if (needsAction) {
                        _pickerSelected     = node.ConditionParam > 0 ? (node.ConditionParam, node.ConditionParamLabel, node.ConditionParamIconId) : null;
                        _pickerNeedsRebuild = true; _pickerSearch = "";
                    } else if (node.ConditionKind == ConditionKind.HasStatus) {
                        _statusPickerSelected     = node.ConditionParam > 0 ? (node.ConditionParam, node.ConditionParamLabel, node.ConditionParamIconId) : null;
                        _statusPickerNeedsRebuild = true; _statusPickerSearch = "";
                    } else if (node.ConditionKind == ConditionKind.JobResource) {
                        _resourceFieldSelected    = (int)node.ConditionParam;
                        _resourceFieldNeedsRebuild = true; _resourceFieldSearch = "";
                    }
                    _openEditCondition = true;
                }
            }
            ImGui.Separator();
            var inGroup   = _selectedNodeIds.Contains(node.Id) && _selectedNodeIds.Count > 1;
            var deleteLabel = inGroup ? $"Delete {_selectedNodeIds.Count} Nodes" : "Delete Node";
            if (ImGui.MenuItem(deleteLabel)) {
                if (inGroup) _pendingDeleteSelection = true;
                else         _pendingDeleteNodeId    = node.Id;
            }
            ImGui.Separator();
            var canCopy  = _selectedNodeIds.Count > 0;
            var canPaste = _clipboard is { Count: > 0 };
            if (!canCopy)  ImGui.BeginDisabled();
            if (ImGui.MenuItem("Copy"))  CopySelected();
            if (!canCopy)  ImGui.EndDisabled();
            if (!canPaste) ImGui.BeginDisabled();
            if (ImGui.MenuItem("Paste")) { _pendingPaste = true; _pendingPastePos = null; }
            if (!canPaste) ImGui.EndDisabled();
            ImGui.EndPopup();
        }
    }

    private static void DrawConditionNodeBody(ImDrawListPtr dl, Vector2 p, FlowNode node) {
        if (node.ConditionParamIconId != 0) {
            try {
                var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(node.ConditionParamIconId)).GetWrapOrEmpty();
                if (wrap != null) {
                    const float maxW = 42f;
                    float maxH = NodeHeight - 10f;
                    float scale = Math.Min(maxW / wrap.Width, maxH / wrap.Height);
                    float dw = wrap.Width * scale, dh = wrap.Height * scale;
                    // shift left to avoid T/F labels on the right edge
                    var iconPos = new Vector2(p.X + (NodeWidth * 0.85f - dw) * 0.5f, p.Y + (NodeHeight - dh) * 0.5f);
                    dl.AddImageRounded(wrap.Handle, iconPos, iconPos + new Vector2(dw, dh),
                        Vector2.Zero, Vector2.One, 0xFFFFFFFF, NodeRound * 0.5f, ImDrawFlags.RoundCornersAll);
                }
            } catch { }
        }

        // T / F port labels
        var ts = ImGui.CalcTextSize("T");
        dl.AddText(new Vector2(p.X + NodeWidth - ts.X - PortRadius * 2 - 4f, p.Y + NodeHeight * 0.25f - ts.Y * 0.5f), TrueCol, "T");
        var fs = ImGui.CalcTextSize("F");
        dl.AddText(new Vector2(p.X + NodeWidth - fs.X - PortRadius * 2 - 4f, p.Y + NodeHeight * 0.75f - fs.Y * 0.5f), FalseC, "F");
    }

    private static void DrawNodeLabel(ImDrawListPtr dl, Vector2 p, FlowNode node) {
        var nodeMidX = p.X + NodeWidth * 0.5f;
        const float gap = 4f;

        if (node.Type == NodeType.Group) {
            var tsR = ImGui.CalcTextSize("ROUTE");
            var l2  = $"Priority {node.Priority}";
            var ts2 = ImGui.CalcTextSize(l2);
            float prioY  = p.Y - gap - ts2.Y;
            float routeY = prioY - 2f - tsR.Y;
            dl.AddText(new Vector2(nodeMidX - tsR.X * 0.5f, routeY), Col(0.769f, 0.690f, 0.961f), "ROUTE");
            dl.AddText(new Vector2(nodeMidX - ts2.X * 0.5f, prioY),  Text2,                        l2);
        } else if (node.Type == NodeType.Condition) {
            string kindStr = node.ConditionKind switch {
                ConditionKind.LastAction        => "LAST ACT",
                ConditionKind.HasStatus         => "HAS STATUS",
                ConditionKind.CooldownReady     => "CD READY",
                ConditionKind.InCombat          => "IN COMBAT",
                ConditionKind.WeaponDrawn       => "WEAPON OUT",
                ConditionKind.ActionUsable      => "USABLE",
                ConditionKind.ActionHighlighted => "HIGHLIGHT",
                ConditionKind.ActionInRange     => "IN RANGE",
                ConditionKind.TargetInLoS       => "IN LOS",
                ConditionKind.JobResource       => "JOB RES",
                ConditionKind.CanWeave          => "CAN WEAVE",
                _                               => "IF",
            };
            var ks = ImGui.CalcTextSize(kindStr);
            dl.AddText(new Vector2(nodeMidX - ks.X * 0.5f, p.Y - gap - ks.Y), Orange, kindStr);
            string subLabel = node.ConditionKind == ConditionKind.CanWeave
                ? node.ConditionParam switch { 1 => "EARLY", 2 => "LATE", _ => "ANY" }
                : node.ConditionParamLabel;
            if (subLabel.Length > 0) {
                var pl = ImGui.CalcTextSize(subLabel);
                dl.AddText(new Vector2(nodeMidX - pl.X * 0.5f, p.Y + NodeHeight + 2f), Text2, subLabel);
            }
        } else {
            var label = node.ActionLabel.Length > 0 ? node.ActionLabel : $"ID:{node.ActionId}";
            var ts    = ImGui.CalcTextSize(label);
            var col   = node.Type == NodeType.Trigger ? Success : Text1;
            dl.AddText(new Vector2(nodeMidX - ts.X * 0.5f, p.Y - gap - ts.Y), col, label);
        }
    }

    private void DrawPorts(ImDrawListPtr dl, Vector2 origin, FlowNode node) {
        if (node.Type != NodeType.Trigger) {
            var ip = InputPort(origin, node);
            dl.AddCircleFilled(ip, PortRadius,      Bg2);
            dl.AddCircle(ip,       PortRadius,      BorderB, 12, 1.5f);
            dl.AddCircle(ip,       PortRadius - 2f, Text2,   12, 1f);
        }

        if (node.Type == NodeType.Condition) {
            var tp = TruePort(origin, node);
            dl.AddCircleFilled(tp, PortRadius,      Bg2);
            dl.AddCircle(tp,       PortRadius,      TrueCol, 12, 1.5f);
            dl.AddCircle(tp,       PortRadius - 2f, TrueCol, 12, 1f);

            var fp = FalsePort(origin, node);
            dl.AddCircleFilled(fp, PortRadius,      Bg2);
            dl.AddCircle(fp,       PortRadius,      FalseC,  12, 1.5f);
            dl.AddCircle(fp,       PortRadius - 2f, FalseC,  12, 1f);
        } else {
            var op = OutputPort(origin, node);
            dl.AddCircleFilled(op, PortRadius,      Bg2);
            dl.AddCircle(op,       PortRadius,      BorderB, 12, 1.5f);
            dl.AddCircle(op,       PortRadius - 2f, Text2,   12, 1f);
        }
    }

    // ──────────────────────────── Edges ────────────────────────────

    private void DrawEdge(ImDrawListPtr dl, Vector2 origin, FlowEdge edge) {
        var fromNode = _flow!.Nodes.Find(n => n.Id == edge.FromNodeId);
        var toNode   = _flow.Nodes.Find(n => n.Id == edge.ToNodeId);
        if (fromNode == null || toNode == null) return;

        var from = FromPort(origin, fromNode, edge.Branch);
        var to   = InputPort(origin, toNode);

        uint edgeCol = edge.Branch == true  ? TrueCol
                     : edge.Branch == false ? FalseC
                     : BorderB;

        if (_animEnabled && FlowExecutor.EdgeTrace.TryGetValue(edge.Id, out var edgeTick)) {
            var now        = Environment.TickCount64;
            var age        = (now - edgeTick.Tick)        / 1000f;
            var settled    = (now - edgeTick.StreakStart) / 1000f;
            if (age < 0.1f && settled >= 0.2f) {
                var et     = Environment.TickCount64 / 1000f;
                var epulse = 0.55f + 0.45f * MathF.Abs(MathF.Sin(et * MathF.PI * 2.8f));
                var ea     = epulse;
                Bezier(dl, from, to, Col(0.455f, 0.765f, 1f, ea * 0.35f), 8f);
                Bezier(dl, from, to, Col(0.455f, 0.765f, 1f, ea),         3f);
            }
        }

        Bezier(dl, from, to, edgeCol, 2f);

        var mid  = Vector2.Lerp(from, to, 0.5f);
        bool hov = Dist(ImGui.GetMousePos(), mid) <= 8f;
        dl.AddCircleFilled(mid, 8f, hov ? Col(1f, 0.522f, 0.569f, 0.20f) : Col(0.145f, 0.149f, 0.169f, 0.90f));
        dl.AddCircle(mid,       8f, hov ? Err : BorderB, 12, 1f);
        dl.AddText(mid + new Vector2(-4f, -7f), hov ? Err : Text2, "×");
    }

    private void DrawPendingEdge(ImDrawListPtr dl, Vector2 origin) {
        var fromNode = _flow!.Nodes.Find(n => n.Id == _pendingFrom);
        if (fromNode == null) return;
        var from = FromPort(origin, fromNode, _pendingFromBranch);
        uint col = _pendingFromBranch == true  ? TrueCol
                 : _pendingFromBranch == false ? FalseC
                 : Col(0.941f, 0.776f, 0.549f, 0.85f);
        Bezier(dl, from, ImGui.GetMousePos(), col, 1.5f);
    }

    // ──────────────────────────── Input ────────────────────────────

    private void HandleInput(Vector2 origin, Vector2 size) {
        var mouse    = ImGui.GetMousePos();
        var io       = ImGui.GetIO();
        bool inCanvas = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                     && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;

        if (_pendingFrom != null && ImGui.IsKeyPressed(ImGuiKey.Escape)) { _pendingFrom = null; _pendingFromBranch = null; return; }

        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return;

        if (_draggingNode == null) {
            if (inCanvas && ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) _canvasOffset += io.MouseDelta;
            if (inCanvas && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && io.KeyAlt)    _canvasOffset += io.MouseDelta;
        }

        if (!inCanvas && _draggingNode == null) return;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt && !_nodePopupOpen) {
            // Edge × hit
            foreach (var edge in _flow!.Edges) {
                var fn = _flow.Nodes.Find(n => n.Id == edge.FromNodeId);
                var tn = _flow.Nodes.Find(n => n.Id == edge.ToNodeId);
                if (fn == null || tn == null) continue;
                if (Dist(mouse, Vector2.Lerp(FromPort(origin, fn, edge.Branch), InputPort(origin, tn), 0.5f)) <= 8f) {
                    _pendingDeleteEdgeId = edge.Id; return;
                }
            }

            // Output / True / False port hit
            foreach (var node in _flow!.Nodes) {
                if (node.Type == NodeType.Condition) {
                    if (Dist(mouse, TruePort(origin, node)) <= PortHitR) {
                        _pendingFrom = node.Id; _pendingFromBranch = true; return;
                    }
                    if (Dist(mouse, FalsePort(origin, node)) <= PortHitR) {
                        _pendingFrom = node.Id; _pendingFromBranch = false; return;
                    }
                } else {
                    if (Dist(mouse, OutputPort(origin, node)) <= PortHitR) {
                        if (_pendingFrom != null) { _pendingFrom = null; _pendingFromBranch = null; }
                        else { _pendingFrom = node.Id; _pendingFromBranch = null; }
                        return;
                    }
                }
            }

            // Input port hit (while wiring)
            if (_pendingFrom != null) {
                foreach (var node in _flow!.Nodes) {
                    if (node.Type == NodeType.Trigger) continue;
                    if (node.Id == _pendingFrom) continue;
                    if (Dist(mouse, InputPort(origin, node)) <= PortHitR) {
                        _flow.Edges.Add(new FlowEdge { FromNodeId = _pendingFrom, ToNodeId = node.Id, Branch = _pendingFromBranch });
                        _pendingFrom = null; _pendingFromBranch = null;
                        _config.Save();
                        return;
                    }
                }
                _autoConnectFrom      = _pendingFrom;
                _autoConnectBranch    = _pendingFromBranch;
                _contextMenuCanvasPos = mouse - origin - _canvasOffset;
                _pendingFrom          = null;
                _pendingFromBranch    = null;
                _openCanvasCtx        = true;
                return;
            }

            // Node body drag / select
            for (var i = _flow!.Nodes.Count - 1; i >= 0; i--) {
                var node = _flow.Nodes[i];
                var np   = NodeScreenPos(origin, node);
                if (!InRect(mouse, np, NodeSize(node))) continue;
                if (io.KeyCtrl) {
                    if (_selectedNodeIds.Contains(node.Id)) _selectedNodeIds.Remove(node.Id);
                    else                                     _selectedNodeIds.Add(node.Id);
                    return;
                }
                if (!_selectedNodeIds.Contains(node.Id))
                    _selectedNodeIds = new HashSet<string> { node.Id };
                else if (_selectedNodeIds.Count > 1)
                    _pendingClearToSingle = true;
                _draggingNode = node;
                return;
            }
            // Click on empty canvas
            if (!io.KeyCtrl) _selectedNodeIds.Clear();
            _isRectSelecting = true;
            _rectSelectStart = mouse;
            _rectSelectEnd   = mouse;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && inCanvas) {
            bool hitNode = false;
            foreach (var node in _flow!.Nodes)
                if (InRect(mouse, NodeScreenPos(origin, node), NodeSize(node))) { hitNode = true; break; }
            if (!hitNode) {
                _autoConnectFrom      = null;
                _autoConnectBranch    = null;
                _contextMenuCanvasPos = mouse - origin - _canvasOffset;
                _openCanvasCtx        = true;
            }
        }

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && !io.KeyAlt) {
            if (_draggingNode != null) {
                _pendingClearToSingle = false;
                var moveAll = _selectedNodeIds.Contains(_draggingNode.Id) && _selectedNodeIds.Count > 1;
                var targets = moveAll
                    ? _flow!.Nodes.Where(n => _selectedNodeIds.Contains(n.Id))
                    : Enumerable.Repeat(_draggingNode, 1);
                foreach (var n in targets) { n.X += io.MouseDelta.X; n.Y += io.MouseDelta.Y; }
            }
            if (_isRectSelecting) _rectSelectEnd = mouse;
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
            if (_draggingNode != null) {
                if (_pendingClearToSingle) {
                    _selectedNodeIds      = new HashSet<string> { _draggingNode.Id };
                    _pendingClearToSingle = false;
                } else {
                    const float snap    = 40f;
                    var         moveAll = _selectedNodeIds.Contains(_draggingNode.Id) && _selectedNodeIds.Count > 1;
                    var targets = moveAll
                        ? _flow!.Nodes.Where(n => _selectedNodeIds.Contains(n.Id))
                        : Enumerable.Repeat(_draggingNode, 1);
                    foreach (var n in targets) {
                        n.X = MathF.Round(n.X / snap) * snap;
                        n.Y = MathF.Round(n.Y / snap) * snap;
                    }
                    _config.Save();
                }
                _draggingNode = null;
            }
            if (_isRectSelecting) {
                var lo = new Vector2(MathF.Min(_rectSelectStart.X, _rectSelectEnd.X),
                                     MathF.Min(_rectSelectStart.Y, _rectSelectEnd.Y));
                var hi = new Vector2(MathF.Max(_rectSelectStart.X, _rectSelectEnd.X),
                                     MathF.Max(_rectSelectStart.Y, _rectSelectEnd.Y));
                if (!io.KeyCtrl) _selectedNodeIds.Clear();
                foreach (var node in _flow!.Nodes) {
                    var np  = NodeScreenPos(origin, node);
                    var nsz = NodeSize(node);
                    if (np.X < hi.X && np.X + nsz.X > lo.X && np.Y < hi.Y && np.Y + nsz.Y > lo.Y)
                        _selectedNodeIds.Add(node.Id);
                }
                _isRectSelecting = false;
            }
        }
    }

    // ──────────────────────────── Popups ────────────────────────────

    private void DrawPopups() {
        if (_openCanvasCtx)          { ImGui.OpenPopup("##canvasCtx");     _openCanvasCtx          = false; }
        if (_openAddTrigger)         { ImGui.OpenPopup("##popTrigger");    _openAddTrigger         = false; }
        if (_openAddAction)          { ImGui.OpenPopup("##popAction");     _openAddAction          = false; }
        if (_openAddGroup)           { ImGui.OpenPopup("##popGroup");      _openAddGroup           = false; }
        if (_openAddCondition)       { ImGui.OpenPopup("##popCondition");  _openAddCondition       = false; }
        if (_openAddJobCondition)       { ImGui.OpenPopup("##popJobCond");     _openAddJobCondition    = false; }
        if (_openAddWeaveCondition)     { ImGui.OpenPopup("##popWeaveCond");   _openAddWeaveCondition  = false; }
        if (_openEditCondition)         { ImGui.OpenPopup("##popEditCond");    _openEditCondition      = false; }

        if (ImGui.BeginPopup("##canvasCtx")) {
            if (ImGui.MenuItem("Add Trigger"))        { _pickerNeedsRebuild = true; _pickerSelected = null; _pickerSearch = ""; _openAddTrigger = true; }
            if (ImGui.MenuItem("Add Action"))         { _pickerNeedsRebuild = true; _pickerSelected = null; _pickerSearch = ""; _openAddAction  = true; }
            if (ImGui.MenuItem("Add Priority Group")) { _addGroupPriority = 1; _openAddGroup = true; }
            if (ImGui.MenuItem("Add Condition"))      { _pickerNeedsRebuild = true; _pickerSelected = null; _pickerSearch = ""; _statusPickerNeedsRebuild = true; _statusPickerSelected = null; _statusPickerSearch = ""; _condKind = 0; _openAddCondition = true; }
            if (ImGui.MenuItem("Add Job Condition"))  { _resourceFieldNeedsRebuild = true; _resourceFieldSelected = -1; _resourceFieldSearch = ""; _condCompareOp = 4; _condCompareVal = 0f; _openAddJobCondition = true; }
            if (ImGui.MenuItem("Add Weave Check")) { _weaveMode = 0; _openAddWeaveCondition = true; }
            ImGui.Separator();
            var canCopy  = _selectedNodeIds.Count > 0;
            var canPaste = _clipboard is { Count: > 0 };
            if (!canCopy)  ImGui.BeginDisabled();
            if (ImGui.MenuItem("Copy"))  CopySelected();
            if (!canCopy)  ImGui.EndDisabled();
            if (!canPaste) ImGui.BeginDisabled();
            if (ImGui.MenuItem("Paste")) { _pendingPaste = true; _pendingPastePos = _contextMenuCanvasPos; }
            if (!canPaste) ImGui.EndDisabled();
            if (_autoConnectFrom != null) {
                ImGui.Separator();
                if (ImGui.MenuItem("Cancel Wire")) { _autoConnectFrom = null; _autoConnectBranch = null; }
            }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("##popTrigger"))   { AddActionPopup(NodeType.Trigger); ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popAction"))    { AddActionPopup(NodeType.Action);  ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popGroup"))     { AddGroupPopup();                  ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popCondition")) { AddConditionPopup();              ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popJobCond"))     { AddJobConditionPopup();             ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popWeaveCond")) { AddWeaveConditionPopup(); ImGui.EndPopup(); }
        if (ImGui.BeginPopup("##popEditCond"))  { EditConditionPopup();    ImGui.EndPopup(); }
    }

    private void AddActionPopup(NodeType type) {
        ImGui.Text(type == NodeType.Trigger ? "Add Trigger Node" : "Add Action Node");
        ImGui.Separator();

        if (_pickerNeedsRebuild) { BuildJobActionCache(); _pickerNeedsRebuild = false; }

        ImGui.SetNextItemWidth(280f);
        ImGui.InputText("##psearch", ref _pickerSearch, 64);

        ImGui.BeginChild("##plist", new Vector2(300, 300), true);
        foreach (var (id, name, level, iconId) in _pickerCache) {
            if (_pickerSearch.Length > 0 && !name.Contains(_pickerSearch, StringComparison.OrdinalIgnoreCase)) continue;
            var wrap   = GetIconWrap(iconId);
            const float iSz = 36f;
            var scrPos = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable($"##a{id}", _pickerSelected?.id == id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, iSz)))
                _pickerSelected = (id, name, iconId);
            var wdl = ImGui.GetWindowDrawList();
            if (wrap != null) wdl.AddImage(wrap.Handle, scrPos, scrPos + new Vector2(iSz, iSz));
            uint tCol = _pickerSelected?.id == id ? ImGui.GetColorU32(new Vector4(0.455f, 0.765f, 1f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
            wdl.AddText(new Vector2(scrPos.X + iSz + 6f, scrPos.Y + (iSz - ImGui.GetFontSize()) / 2f), tCol, $"{name}  Lv{level}");
        }
        ImGui.EndChild();

        if (_pickerSelected.HasValue)
            ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"Selected: {_pickerSelected.Value.name} (ID: {_pickerSelected.Value.id})");
        else
            ImGui.TextDisabled(_pickerCache.Count == 0 ? "Not in-game or job not loaded." : "Select an action.");

        ImGui.Spacing();
        if (!_pickerSelected.HasValue) ImGui.BeginDisabled();
        if (ImGui.Button("Add##act")) {
            var node = new FlowNode {
                Type        = type,
                ActionId    = _pickerSelected!.Value.id,
                ActionLabel = _pickerSelected.Value.name,
                IconId      = _pickerSelected.Value.iconId,
                X           = _contextMenuCanvasPos.X,
                Y           = _contextMenuCanvasPos.Y,
            };
            _flow!.Nodes.Add(node);
            if (_autoConnectFrom != null && type != NodeType.Trigger) {
                _flow.Edges.Add(new FlowEdge { FromNodeId = _autoConnectFrom, ToNodeId = node.Id, Branch = _autoConnectBranch });
                _autoConnectFrom = null; _autoConnectBranch = null;
            }
            _config.Save();
            _pickerSelected = null; _pickerSearch = "";
            ImGui.CloseCurrentPopup();
        }
        if (!_pickerSelected.HasValue) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##act")) { _pickerSelected = null; _pickerSearch = ""; _autoConnectFrom = null; _autoConnectBranch = null; ImGui.CloseCurrentPopup(); }
    }

    private void AddGroupPopup() {
        ImGui.Text("Add Priority Group");
        ImGui.Separator();
        ImGui.TextDisabled("Wire Trigger → Group → Actions. Lower number fires first.");
        ImGui.Spacing();
        ImGui.Text("Priority:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.InputInt("##gprio", ref _addGroupPriority);
        _addGroupPriority = Math.Max(1, _addGroupPriority);
        ImGui.Spacing();
        if (ImGui.Button("Add##grp")) {
            var node = new FlowNode {
                Type     = NodeType.Group,
                Priority = _addGroupPriority,
                X        = _contextMenuCanvasPos.X,
                Y        = _contextMenuCanvasPos.Y,
            };
            _flow!.Nodes.Add(node);
            if (_autoConnectFrom != null) {
                _flow.Edges.Add(new FlowEdge { FromNodeId = _autoConnectFrom, ToNodeId = node.Id, Branch = _autoConnectBranch });
                _autoConnectFrom = null; _autoConnectBranch = null;
            }
            _config.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##grp")) { _autoConnectFrom = null; _autoConnectBranch = null; ImGui.CloseCurrentPopup(); }
    }

    private void AddConditionPopup() {
        ImGui.Text("Add Condition Node");
        ImGui.Separator();

        ImGui.SetNextItemWidth(180f);
        ImGui.Combo("Kind##ckind", ref _condKind, CondKindLabels, CondKindLabels.Length);

        var kind = (ConditionKind)_condKind;
        bool needsAction  = kind is ConditionKind.LastAction or ConditionKind.CooldownReady
                                 or ConditionKind.ActionUsable or ConditionKind.ActionHighlighted
                                 or ConditionKind.ActionInRange or ConditionKind.TargetInLoS;
        bool needsStatus  = kind == ConditionKind.HasStatus;
        bool needsCompare = kind == ConditionKind.CooldownReady;

        ImGui.Spacing();

        if (needsAction) {
            ImGui.Text("Action:");
            if (_pickerNeedsRebuild) { BuildJobActionCache(); _pickerNeedsRebuild = false; }
            ImGui.SetNextItemWidth(200f);
            ImGui.InputText("##cpsearch", ref _pickerSearch, 64);
            ImGui.BeginChild("##cplist", new Vector2(260, 240), true);
            foreach (var (id, name, level, iconId) in _pickerCache) {
                if (_pickerSearch.Length > 0 && !name.Contains(_pickerSearch, StringComparison.OrdinalIgnoreCase)) continue;
                var wrap   = GetIconWrap(iconId);
                const float iSz = 36f;
                var scrPos = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##ca{id}", _pickerSelected?.id == id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, iSz)))
                    _pickerSelected = (id, name, iconId);
                var wdl = ImGui.GetWindowDrawList();
                if (wrap != null) wdl.AddImage(wrap.Handle, scrPos, scrPos + new Vector2(iSz, iSz));
                uint tCol = _pickerSelected?.id == id ? ImGui.GetColorU32(new Vector4(0.455f, 0.765f, 1f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
                wdl.AddText(new Vector2(scrPos.X + iSz + 6f, scrPos.Y + (iSz - ImGui.GetFontSize()) / 2f), tCol, $"{name}  Lv{level}");
            }
            ImGui.EndChild();
            if (_pickerSelected.HasValue)
                ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"{_pickerSelected.Value.name}");
            else
                ImGui.TextDisabled("Select an action.");
        }

        if (needsStatus) {
            ImGui.Text("Status:");
            if (_statusPickerNeedsRebuild) { BuildStatusCache(); _statusPickerNeedsRebuild = false; }
            ImGui.SetNextItemWidth(200f);
            ImGui.InputText("##spsearch", ref _statusPickerSearch, 64);
            ImGui.BeginChild("##splist", new Vector2(240, 180), true);
            foreach (var (id, name, iconId) in _statusPickerCache) {
                bool matchName = _statusPickerSearch.Length == 0 || name.Contains(_statusPickerSearch, StringComparison.OrdinalIgnoreCase);
                bool matchId   = uint.TryParse(_statusPickerSearch, out uint searchId) && id == searchId;
                if (!matchName && !matchId) continue;
                var   wrap   = GetIconWrap(iconId);
                const float iW = 36f;
                float iH       = wrap != null ? iW * ((float)wrap.Height / wrap.Width) : 48f;
                var   scrPos = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##sp{id}", _statusPickerSelected?.id == id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, iH)))
                    _statusPickerSelected = (id, name, iconId);
                var wdl = ImGui.GetWindowDrawList();
                if (wrap != null) wdl.AddImage(wrap.Handle, scrPos, scrPos + new Vector2(iW, iH));
                uint tCol = _statusPickerSelected?.id == id ? ImGui.GetColorU32(new Vector4(0.455f, 0.765f, 1f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
                wdl.AddText(new Vector2(scrPos.X + iW + 4f, scrPos.Y + (iH - ImGui.GetFontSize()) / 2f), tCol, $"{name}  [{id}]");
            }
            ImGui.EndChild();
            if (_statusPickerSelected.HasValue)
                ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"{_statusPickerSelected.Value.name} (ID: {_statusPickerSelected.Value.id})");
            else
                ImGui.TextDisabled("Select a status.");
            ImGui.Text("Source:");
            ImGui.SameLine();
            ImGui.RadioButton("Player##csrc", ref _condStatusSrc, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Target##csrc", ref _condStatusSrc, 1);
        }

        if (needsCompare) {
            ImGui.Text("Remaining CD");
            ImGui.SetNextItemWidth(70f);
            ImGui.Combo("##ccop", ref _condCompareOp, CompareOpLabels, CompareOpLabels.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputFloat("sec##ccval", ref _condCompareVal, 0.5f, 1f, "%.1f");
        }

        ImGui.Spacing();
        bool canAdd = (!needsAction || _pickerSelected.HasValue) && (!needsStatus || _statusPickerSelected.HasValue);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add##cond")) {
            var node = new FlowNode {
                Type      = NodeType.Condition,
                X         = _contextMenuCanvasPos.X,
                Y         = _contextMenuCanvasPos.Y,
                ConditionKind   = kind,
                ConditionSource = (StatusSource)_condStatusSrc,
                ConditionParam  = needsAction ? _pickerSelected!.Value.id
                                : needsStatus ? _statusPickerSelected!.Value.id
                                : 0,
                ConditionParamLabel  = needsAction ? _pickerSelected!.Value.name
                                     : needsStatus ? _statusPickerSelected!.Value.name
                                     : "",
                ConditionParamIconId = needsAction ? _pickerSelected!.Value.iconId
                                     : needsStatus ? _statusPickerSelected!.Value.iconId
                                     : 0,
                CompareOp  = _condCompareOp,
                CompareVal = _condCompareVal,
            };
            _flow!.Nodes.Add(node);
            if (_autoConnectFrom != null) {
                _flow.Edges.Add(new FlowEdge { FromNodeId = _autoConnectFrom, ToNodeId = node.Id, Branch = _autoConnectBranch });
                _autoConnectFrom = null; _autoConnectBranch = null;
            }
            _config.Save();
            _pickerSelected = null; _pickerSearch = "";
            _statusPickerSelected = null; _statusPickerSearch = "";
            ImGui.CloseCurrentPopup();
        }
        if (!canAdd) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##cond")) {
            _autoConnectFrom = null; _autoConnectBranch = null;
            _pickerSelected = null; _pickerSearch = "";
            _statusPickerSelected = null; _statusPickerSearch = "";
            ImGui.CloseCurrentPopup();
        }
    }

    private void AddJobConditionPopup() {
        ImGui.Text("Add Job Condition Node");
        ImGui.Separator();

        if (_resourceFieldNeedsRebuild) { BuildResourceFieldCache(); _resourceFieldNeedsRebuild = false; }

        if (_resourceFieldCache.Count == 0) {
            ImGui.TextDisabled("No resources for this job — ensure the flow has a job set.");
            ImGui.Spacing();
            if (ImGui.Button("Cancel##jcond")) { _autoConnectFrom = null; _autoConnectBranch = null; ImGui.CloseCurrentPopup(); }
            return;
        }

        ImGui.SetNextItemWidth(240f);
        ImGui.InputText("##jrfsearch", ref _resourceFieldSearch, 64);
        ImGui.BeginChild("##jrflist", new Vector2(280, 180), true);
        foreach (var (index, name, maxVal) in _resourceFieldCache) {
            if (_resourceFieldSearch.Length > 0 && !name.Contains(_resourceFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
            bool sel   = _resourceFieldSelected == index;
            string lbl = $"{name}  (0–{maxVal:0.#})##jrf{index}";
            if (ImGui.Selectable(lbl, sel, ImGuiSelectableFlags.None, new Vector2(0, 16)))
                _resourceFieldSelected = index;
        }
        ImGui.EndChild();

        if (_resourceFieldSelected >= 0 && _resourceFieldSelected < _resourceFieldCache.Count) {
            var (_, selName, _) = _resourceFieldCache[_resourceFieldSelected];
            ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"Selected: {selName}");
            ImGui.SetNextItemWidth(70f);
            ImGui.Combo("##jrcop", ref _condCompareOp, CompareOpLabels, CompareOpLabels.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputFloat("##jrcval", ref _condCompareVal, 0.1f, 1f, "%.1f");
        } else {
            ImGui.TextDisabled("Select a resource field.");
        }

        ImGui.Spacing();
        bool canAdd = _resourceFieldSelected >= 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("Add##jcond")) {
            var node = new FlowNode {
                Type                 = NodeType.Condition,
                X                    = _contextMenuCanvasPos.X,
                Y                    = _contextMenuCanvasPos.Y,
                ConditionKind        = ConditionKind.JobResource,
                ConditionParam       = (uint)_resourceFieldSelected,
                ConditionParamLabel  = _resourceFieldCache[_resourceFieldSelected].Name,
                ConditionParamIconId = 0,
                CompareOp            = _condCompareOp,
                CompareVal           = _condCompareVal,
            };
            _flow!.Nodes.Add(node);
            if (_autoConnectFrom != null) {
                _flow.Edges.Add(new FlowEdge { FromNodeId = _autoConnectFrom, ToNodeId = node.Id, Branch = _autoConnectBranch });
                _autoConnectFrom = null; _autoConnectBranch = null;
            }
            _config.Save();
            _resourceFieldSelected = -1; _resourceFieldSearch = ""; _resourceFieldNeedsRebuild = true;
            ImGui.CloseCurrentPopup();
        }
        if (!canAdd) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##jcond")) {
            _autoConnectFrom = null; _autoConnectBranch = null;
            _resourceFieldSelected = -1; _resourceFieldSearch = ""; _resourceFieldNeedsRebuild = true;
            ImGui.CloseCurrentPopup();
        }
    }

    private void AddWeaveConditionPopup() {
        ImGui.Text("Add Weave Check");
        ImGui.Separator();
        ImGui.Text("Weave Window:");
        ImGui.RadioButton("Any##wma",   ref _weaveMode, 0); ImGui.SameLine();
        ImGui.RadioButton("Early##wma", ref _weaveMode, 1); ImGui.SameLine();
        ImGui.RadioButton("Late##wma",  ref _weaveMode, 2);
        ImGui.Spacing();
        if (ImGui.Button("Add##weavecond")) {
            var node = new FlowNode {
                Type           = NodeType.Condition,
                X              = _contextMenuCanvasPos.X,
                Y              = _contextMenuCanvasPos.Y,
                ConditionKind  = ConditionKind.CanWeave,
                ConditionParam = (uint)_weaveMode,
            };
            _flow!.Nodes.Add(node);
            if (_autoConnectFrom != null) {
                _flow.Edges.Add(new FlowEdge { FromNodeId = _autoConnectFrom, ToNodeId = node.Id, Branch = _autoConnectBranch });
                _autoConnectFrom = null; _autoConnectBranch = null;
            }
            _config.Save();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##weavecond")) {
            _autoConnectFrom = null; _autoConnectBranch = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void EditConditionPopup() {
        var node = _editConditionNode!;
        ImGui.Text("Edit Condition");
        ImGui.Separator();

        bool needsAction  = node.ConditionKind is ConditionKind.LastAction or ConditionKind.CooldownReady
                                               or ConditionKind.ActionUsable or ConditionKind.ActionHighlighted
                                               or ConditionKind.ActionInRange or ConditionKind.TargetInLoS;
        bool needsStatus  = node.ConditionKind == ConditionKind.HasStatus;
        bool needsCompare = node.ConditionKind == ConditionKind.CooldownReady;
        bool noParams     = node.ConditionKind is ConditionKind.InCombat or ConditionKind.WeaponDrawn;

        ImGui.TextDisabled(node.ConditionKind.ToString());
        ImGui.Spacing();

        if (noParams) {
            ImGui.TextDisabled("No editable parameters.");
            ImGui.Spacing();
            if (ImGui.Button("Close##editcond")) ImGui.CloseCurrentPopup();
            return;
        }

        if (node.ConditionKind == ConditionKind.CanWeave) {
            ImGui.Text("Weave Window:");
            int mode = (int)node.ConditionParam;
            if (ImGui.RadioButton("Any##wm",   ref mode, 0)) { }
            ImGui.SameLine();
            if (ImGui.RadioButton("Early##wm", ref mode, 1)) { }
            ImGui.SameLine();
            if (ImGui.RadioButton("Late##wm",  ref mode, 2)) { }
            ImGui.Spacing();
            if (ImGui.Button("Save##editcond")) {
                node.ConditionParam = (uint)mode;
                _config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##editcond")) ImGui.CloseCurrentPopup();
            return;
        }

        if (node.ConditionKind == ConditionKind.JobResource) {
            if (_resourceFieldNeedsRebuild) { BuildResourceFieldCache(); _resourceFieldNeedsRebuild = false; }
            if (_resourceFieldCache.Count == 0) {
                ImGui.TextDisabled("No resources for this job.");
                if (ImGui.Button("Cancel##editcond")) ImGui.CloseCurrentPopup();
                return;
            }
            ImGui.SetNextItemWidth(240f);
            ImGui.InputText("##ejrfsearch", ref _resourceFieldSearch, 64);
            ImGui.BeginChild("##ejrflist", new Vector2(280, 160), true);
            foreach (var (index, name, maxVal) in _resourceFieldCache) {
                if (_resourceFieldSearch.Length > 0 && !name.Contains(_resourceFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable($"{name}  (0–{maxVal:0.#})##ejrf{index}", _resourceFieldSelected == index, ImGuiSelectableFlags.None, new Vector2(0, 16)))
                    _resourceFieldSelected = index;
            }
            ImGui.EndChild();
            if (_resourceFieldSelected >= 0 && _resourceFieldSelected < _resourceFieldCache.Count) {
                var (_, selName, _) = _resourceFieldCache[_resourceFieldSelected];
                ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"Selected: {selName}");
                ImGui.SetNextItemWidth(70f);
                ImGui.Combo("##ejrcop", ref _condCompareOp, CompareOpLabels, CompareOpLabels.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                ImGui.InputFloat("##ejrcval", ref _condCompareVal, 0.1f, 1f, "%.1f");
            } else {
                ImGui.TextDisabled("Select a resource field.");
            }
            ImGui.Spacing();
            bool canSaveJr = _resourceFieldSelected >= 0;
            if (!canSaveJr) ImGui.BeginDisabled();
            if (ImGui.Button("Save##editcond")) {
                node.ConditionParam      = (uint)_resourceFieldSelected;
                node.ConditionParamLabel = _resourceFieldCache[_resourceFieldSelected].Name;
                node.CompareOp           = _condCompareOp;
                node.CompareVal          = _condCompareVal;
                _config.Save();
                _resourceFieldSelected = -1; _resourceFieldSearch = ""; _resourceFieldNeedsRebuild = true;
                ImGui.CloseCurrentPopup();
            }
            if (!canSaveJr) ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##editcond")) {
                _resourceFieldSelected = -1; _resourceFieldSearch = ""; _resourceFieldNeedsRebuild = true;
                ImGui.CloseCurrentPopup();
            }
            return;
        }

        // Action-based or HasStatus conditions
        if (needsAction) {
            ImGui.Text("Action:");
            if (_pickerNeedsRebuild) { BuildJobActionCache(); _pickerNeedsRebuild = false; }
            ImGui.SetNextItemWidth(200f);
            ImGui.InputText("##ecpsearch", ref _pickerSearch, 64);
            ImGui.BeginChild("##ecplist", new Vector2(260, 240), true);
            foreach (var (id, name, level, iconId) in _pickerCache) {
                if (_pickerSearch.Length > 0 && !name.Contains(_pickerSearch, StringComparison.OrdinalIgnoreCase)) continue;
                var wrap   = GetIconWrap(iconId);
                const float iSz = 36f;
                var scrPos = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##eca{id}", _pickerSelected?.id == id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, iSz)))
                    _pickerSelected = (id, name, iconId);
                var wdl = ImGui.GetWindowDrawList();
                if (wrap != null) wdl.AddImage(wrap.Handle, scrPos, scrPos + new Vector2(iSz, iSz));
                uint tCol = _pickerSelected?.id == id ? ImGui.GetColorU32(new Vector4(0.455f, 0.765f, 1f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
                wdl.AddText(new Vector2(scrPos.X + iSz + 6f, scrPos.Y + (iSz - ImGui.GetFontSize()) / 2f), tCol, $"{name}  Lv{level}");
            }
            ImGui.EndChild();
            if (_pickerSelected.HasValue)
                ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"{_pickerSelected.Value.name}");
            else
                ImGui.TextDisabled("Select an action.");
        }

        if (needsStatus) {
            ImGui.Text("Status:");
            if (_statusPickerNeedsRebuild) { BuildStatusCache(); _statusPickerNeedsRebuild = false; }
            ImGui.SetNextItemWidth(200f);
            ImGui.InputText("##espsearch", ref _statusPickerSearch, 64);
            ImGui.BeginChild("##esplist", new Vector2(240, 160), true);
            foreach (var (id, name, iconId) in _statusPickerCache) {
                bool matchName = _statusPickerSearch.Length == 0 || name.Contains(_statusPickerSearch, StringComparison.OrdinalIgnoreCase);
                bool matchId   = uint.TryParse(_statusPickerSearch, out uint searchId) && id == searchId;
                if (!matchName && !matchId) continue;
                var   wrap   = GetIconWrap(iconId);
                const float iW = 36f;
                float iH       = wrap != null ? iW * ((float)wrap.Height / wrap.Width) : 48f;
                var   scrPos = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"##esp{id}", _statusPickerSelected?.id == id, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X, iH)))
                    _statusPickerSelected = (id, name, iconId);
                var wdl = ImGui.GetWindowDrawList();
                if (wrap != null) wdl.AddImage(wrap.Handle, scrPos, scrPos + new Vector2(iW, iH));
                uint tCol = _statusPickerSelected?.id == id ? ImGui.GetColorU32(new Vector4(0.455f, 0.765f, 1f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
                wdl.AddText(new Vector2(scrPos.X + iW + 4f, scrPos.Y + (iH - ImGui.GetFontSize()) / 2f), tCol, $"{name}  [{id}]");
            }
            ImGui.EndChild();
            if (_statusPickerSelected.HasValue)
                ImGui.TextColored(new Vector4(0.635f, 0.855f, 0.549f, 1f), $"{_statusPickerSelected.Value.name} (ID: {_statusPickerSelected.Value.id})");
            else
                ImGui.TextDisabled("Select a status.");
            ImGui.Text("Source:");
            ImGui.SameLine();
            ImGui.RadioButton("Player##ecsrc", ref _condStatusSrc, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Target##ecsrc", ref _condStatusSrc, 1);
        }

        if (needsCompare) {
            ImGui.Text("Remaining CD");
            ImGui.SetNextItemWidth(70f);
            ImGui.Combo("##eccop", ref _condCompareOp, CompareOpLabels, CompareOpLabels.Length);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80f);
            ImGui.InputFloat("sec##eccval", ref _condCompareVal, 0.5f, 1f, "%.1f");
        }

        ImGui.Spacing();
        bool canSave = (!needsAction || _pickerSelected.HasValue) && (!needsStatus || _statusPickerSelected.HasValue);
        if (!canSave) ImGui.BeginDisabled();
        if (ImGui.Button("Save##editcond")) {
            node.ConditionSource      = (StatusSource)_condStatusSrc;
            node.ConditionParam       = needsAction ? _pickerSelected!.Value.id
                                      : needsStatus ? _statusPickerSelected!.Value.id
                                      : 0;
            node.ConditionParamLabel  = needsAction ? _pickerSelected!.Value.name
                                      : needsStatus ? _statusPickerSelected!.Value.name
                                      : "";
            node.ConditionParamIconId = needsAction ? _pickerSelected!.Value.iconId
                                      : needsStatus ? _statusPickerSelected!.Value.iconId
                                      : 0;
            node.CompareOp  = _condCompareOp;
            node.CompareVal = _condCompareVal;
            _config.Save();
            _pickerSelected = null; _pickerSearch = "";
            _statusPickerSelected = null; _statusPickerSearch = "";
            ImGui.CloseCurrentPopup();
        }
        if (!canSave) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##editcond")) {
            _pickerSelected = null; _pickerSearch = "";
            _statusPickerSelected = null; _statusPickerSearch = "";
            ImGui.CloseCurrentPopup();
        }
    }

    private void BuildJobActionCache() {
        _pickerCache.Clear();
        _pickerSelected = null;

        uint jobId = 0, parentJobId = 0;

        // Prefer flow's job; fall back to player's current job
        if (_flow?.Job is { Length: > 0 } flowJob) {
            var cjSheet = _dataManager.GetExcelSheet<LuminaClassJob>();
            if (cjSheet != null) {
                foreach (var cj in cjSheet) {
                    if (cj.Abbreviation.ToString().Equals(flowJob, StringComparison.OrdinalIgnoreCase)) {
                        jobId       = cj.RowId;
                        parentJobId = cj.ClassJobParent.RowId;
                        break;
                    }
                }
            }
        }

        if (jobId == 0) {
            if (!Plugin.PlayerState.IsLoaded) return;
            jobId       = Plugin.PlayerState.ClassJob.RowId;
            parentJobId = Plugin.PlayerState.ClassJob.ValueNullable?.ClassJobParent.RowId ?? 0;
        }

        var sheet = _dataManager.GetExcelSheet<LuminaAction>();
        if (sheet == null) return;

        foreach (var action in sheet) {
            var jid = action.ClassJob.RowId;
            if (jid == 0) continue;
            if (jid != jobId && (parentJobId == 0 || jid != parentJobId)) continue;
            if (action.ClassJobLevel == 0) continue;
            var name = action.Name.ToString();
            if (name.Length == 0) continue;
            _pickerCache.Add((action.RowId, name, action.ClassJobLevel, action.Icon));
        }

        _pickerCache.Sort((a, b) => a.Level == b.Level
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : a.Level.CompareTo(b.Level));
    }

    private void BuildResourceFieldCache() {
        _resourceFieldCache.Clear();
        _resourceFieldSelected = -1;
        var ds = JobDataSourceRegistry.GetForJobString(_flow?.Job ?? "");
        if (ds == null) return;
        for (int i = 0; i < ds.ConditionFieldNames.Count; i++)
            _resourceFieldCache.Add((i, ds.ConditionFieldNames[i], ds.GetMaxValue(i)));
    }

    private void BuildStatusCache() {
        _statusPickerCache.Clear();
        _statusPickerSelected = null;
        var sheet = _dataManager.GetExcelSheet<LuminaStatus>();
        if (sheet == null) return;
        foreach (var status in sheet) {
            var name = status.Name.ToString();
            if (name.Length == 0 || status.Icon == 0) continue;
            _statusPickerCache.Add((status.RowId, name, status.Icon));
        }
        _statusPickerCache.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private IDalamudTextureWrap? GetIconWrap(uint iconId) {
        if (iconId == 0) return null;
        try { return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty(); }
        catch { return null; }
    }

    // ──────────────────────────── Port positions ────────────────────────────

    private Vector2 NodeScreenPos(Vector2 origin, FlowNode n) =>
        origin + _canvasOffset + new Vector2(n.X, n.Y);

    private Vector2 InputPort(Vector2 origin, FlowNode n) =>
        NodeScreenPos(origin, n) + new Vector2(0f, NodeHeight * 0.5f);

    private Vector2 OutputPort(Vector2 origin, FlowNode n) =>
        NodeScreenPos(origin, n) + new Vector2(NodeWidth, NodeHeight * 0.5f);

    private Vector2 TruePort(Vector2 origin, FlowNode n) =>
        NodeScreenPos(origin, n) + new Vector2(NodeWidth, NodeHeight * 0.25f);

    private Vector2 FalsePort(Vector2 origin, FlowNode n) =>
        NodeScreenPos(origin, n) + new Vector2(NodeWidth, NodeHeight * 0.75f);

    private Vector2 FromPort(Vector2 origin, FlowNode n, bool? branch) => branch switch {
        true  => TruePort(origin, n),
        false => FalsePort(origin, n),
        _     => OutputPort(origin, n),
    };

    // ──────────────────────────── Helpers ────────────────────────────

    private static void Bezier(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float thickness) {
        var dx = MathF.Abs(b.X - a.X) * 0.5f + 40f;
        dl.AddBezierCubic(a, a + new Vector2(dx, 0), b - new Vector2(dx, 0), b, col, thickness);
    }

    private static float Dist(Vector2 a, Vector2 b) => Vector2.Distance(a, b);

    private static bool InRect(Vector2 p, Vector2 tl, Vector2 sz) =>
        p.X >= tl.X && p.X <= tl.X + sz.X && p.Y >= tl.Y && p.Y <= tl.Y + sz.Y;

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    // ──────────────────────────── Selection ────────────────────────────

    private void DrawSelectionOverlay(ImDrawListPtr dl, Vector2 origin) {
        if (_selectedNodeIds.Count > 0) {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var node in _flow!.Nodes) {
                if (!_selectedNodeIds.Contains(node.Id)) continue;
                if (node.X               < minX) minX = node.X;
                if (node.Y               < minY) minY = node.Y;
                if (node.X + NodeWidth  > maxX) maxX = node.X + NodeWidth;
                if (node.Y + NodeHeight > maxY) maxY = node.Y + NodeHeight;
            }
            const float pad = 6f;
            var min = origin + _canvasOffset + new Vector2(minX - pad, minY - pad);
            var max = origin + _canvasOffset + new Vector2(maxX + pad, maxY + pad);
            dl.AddRectFilled(min, max, Col(0.455f, 0.765f, 1f, 0.05f), 8f);
            DrawDashedRect(dl, min, max, Col(0.455f, 0.765f, 1f, 0.75f));
        }
        if (_isRectSelecting) {
            var lo = new Vector2(MathF.Min(_rectSelectStart.X, _rectSelectEnd.X),
                                 MathF.Min(_rectSelectStart.Y, _rectSelectEnd.Y));
            var hi = new Vector2(MathF.Max(_rectSelectStart.X, _rectSelectEnd.X),
                                 MathF.Max(_rectSelectStart.Y, _rectSelectEnd.Y));
            dl.AddRectFilled(lo, hi, Col(0.455f, 0.765f, 1f, 0.08f));
            DrawDashedRect(dl, lo, hi, Col(0.455f, 0.765f, 1f, 0.75f));
        }
    }

    private static void DrawDashedRect(ImDrawListPtr dl, Vector2 min, Vector2 max,
        uint col, float dash = 6f, float gap = 4f, float thickness = 1.5f, float radius = 8f) {
        var r = MathF.Min(radius, MathF.Min((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f));
        // Straight sides (shortened by r at each end)
        AddDashedLine(dl, new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y), col, dash, gap, thickness);
        AddDashedLine(dl, new Vector2(max.X, min.Y + r), new Vector2(max.X, max.Y - r), col, dash, gap, thickness);
        AddDashedLine(dl, new Vector2(max.X - r, max.Y), new Vector2(min.X + r, max.Y), col, dash, gap, thickness);
        AddDashedLine(dl, new Vector2(min.X, max.Y - r), new Vector2(min.X, min.Y + r), col, dash, gap, thickness);
        // Corner arcs
        AddArc(dl, new Vector2(max.X - r, min.Y + r), r, -MathF.PI * 0.5f, 0f,              col, thickness);
        AddArc(dl, new Vector2(max.X - r, max.Y - r), r, 0f,                MathF.PI * 0.5f, col, thickness);
        AddArc(dl, new Vector2(min.X + r, max.Y - r), r, MathF.PI * 0.5f,  MathF.PI,        col, thickness);
        AddArc(dl, new Vector2(min.X + r, min.Y + r), r, MathF.PI,         MathF.PI * 1.5f, col, thickness);
    }

    private static void AddArc(ImDrawListPtr dl, Vector2 center, float radius,
        float aMin, float aMax, uint col, float thickness, int segments = 8) {
        var step = (aMax - aMin) / segments;
        for (var i = 0; i < segments; i++) {
            var a0 = aMin + step * i;
            var a1 = aMin + step * (i + 1);
            dl.AddLine(
                center + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * radius,
                center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius,
                col, thickness);
        }
    }

    private static void AddDashedLine(ImDrawListPtr dl, Vector2 a, Vector2 b,
        uint col, float dash, float gap, float thickness) {
        var dir = b - a;
        var len = dir.Length();
        if (len < 0.001f) return;
        dir /= len;
        bool drawing = true;
        for (float t = 0f; t < len; ) {
            var seg = drawing ? dash : gap;
            var end = MathF.Min(t + seg, len);
            if (drawing) dl.AddLine(a + dir * t, a + dir * end, col, thickness);
            t      += seg;
            drawing = !drawing;
        }
    }

    private static FlowNode DeepCopyNode(FlowNode n) => new() {
        Id = n.Id, Type = n.Type, X = n.X, Y = n.Y,
        ActionId = n.ActionId, ActionLabel = n.ActionLabel, IconId = n.IconId,
        Priority = n.Priority,
        ConditionKind        = n.ConditionKind,        ConditionSource      = n.ConditionSource,
        ConditionParam       = n.ConditionParam,       ConditionParamLabel  = n.ConditionParamLabel,
        ConditionParamIconId = n.ConditionParamIconId, CompareOp            = n.CompareOp,
        CompareVal           = n.CompareVal,
    };

    private void CopySelected() {
        if (_flow == null || _selectedNodeIds.Count == 0) return;
        var ids = _selectedNodeIds;
        _clipboard      = _flow.Nodes.Where(n => ids.Contains(n.Id)).Select(DeepCopyNode).ToList();
        _clipboardEdges = _flow.Edges
            .Where(e => ids.Contains(e.FromNodeId) && ids.Contains(e.ToNodeId))
            .Select(e => new FlowEdge { FromNodeId = e.FromNodeId, ToNodeId = e.ToNodeId, Branch = e.Branch })
            .ToList();
    }

    private void PasteClipboard(Vector2? targetPos = null) {
        if (_flow == null || _clipboard is not { Count: > 0 }) return;
        var idMap  = _clipboard.ToDictionary(n => n.Id, _ => Guid.NewGuid().ToString());
        Vector2 offset;
        if (targetPos.HasValue) {
            const float snap = 40f;
            var snapped = new Vector2(
                MathF.Round(targetPos.Value.X / snap) * snap,
                MathF.Round(targetPos.Value.Y / snap) * snap);
            var minX = _clipboard.Min(n => n.X);
            var minY = _clipboard.Min(n => n.Y);
            offset = snapped - new Vector2(minX, minY);
        } else {
            offset = new Vector2(40f, 40f);
        }
        var newNodes = _clipboard.Select(n => {
            var c = DeepCopyNode(n); c.Id = idMap[n.Id]; c.X += offset.X; c.Y += offset.Y; return c;
        }).ToList();
        var newEdges = (_clipboardEdges ?? [])
            .Where(e => idMap.ContainsKey(e.FromNodeId) && idMap.ContainsKey(e.ToNodeId))
            .Select(e => new FlowEdge { FromNodeId = idMap[e.FromNodeId], ToNodeId = idMap[e.ToNodeId], Branch = e.Branch })
            .ToList();
        _flow.Nodes.AddRange(newNodes);
        _flow.Edges.AddRange(newEdges);
        _selectedNodeIds = new HashSet<string>(newNodes.Select(n => n.Id));
        _config.Save();
    }
}
