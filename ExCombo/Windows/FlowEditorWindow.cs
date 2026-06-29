using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private string? _wireFromNodeId;
    private int     _wireFromPortIndex;
    private string? _pickerNodeId;
    private string  _pickerSearch     = "";
    private string  _pickerLastSearch = "\0";
    private readonly List<(uint Id, string Name, uint Icon, byte Level, bool IsPvp)> _pickerResults = new();
    private HashSet<uint>? _pickerJobCategoryIds;
    private bool _pickerPvpTab;
    private string? _branchEditNodeId;
    private int     _branchEditCount;
    private Vector2 _contextMenuCanvasPos;
    private string? _pendingDeleteNodeId;
    private string? _draggingNodeId;

    private string? _condEditNodeId;
    private string  _condFieldSearch   = "";
    private string  _condFieldSelected = "";
    private int     _condEditOp;
    private int     _condEditVal;

    // Deferred popup opens — set inside popup contexts, opened outside
    private bool _pendingOpenPicker;
    private bool _pendingOpenBranchEdit;
    private bool _pendingOpenCondEdit;

    private static readonly Dictionary<string, uint> _jobIconCache = new();

    private readonly HashSet<string> _selectedNodeIds = new();
    private bool    _isMarqueeSelecting;
    private Vector2 _marqueeStart;
    private Vector2 _marqueeEnd;

    private List<(string OrigId, NodeType Type, float RelX, float RelY, uint ActionId, string ActionLabel, uint IconId, int OutputCount, string CondField, int CondOp, int CondVal, bool IsOgcd)>? _clipboardNodes;
    private List<(string FromOrig, string ToOrig, int PortIdx)>?                                                                                                                                                                                                   _clipboardEdges;

    private static readonly Vector2 NodeSize    = new(64f, 64f);
    private const           float   PortRadius  = 6f;
    private const           float   GridStep    = 32f;
    private const           float   BranchSlotH = 32f;

    private static bool IconMenuItem(FontAwesomeIcon icon, string label) {
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
            var col  = ImGui.GetColorU32(ImGuiCol.Text);
            var ipos = new Vector2(rMin.X + 4f, rMin.Y + (rMax.Y - rMin.Y - sz) * 0.5f);
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            dl.AddText(ipos, col, iconStr);
            ImGui.PopFont();
        }
        return result;
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
        n.Type is NodeType.Branch or NodeType.Condition
            ? MathF.Max(NodeSize.Y, BranchSlotH * n.OutputCount)
            : NodeSize.Y;

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
    private static uint Bg1 => Col(0.102f, 0.106f, 0.118f);

    public FlowEditorWindow(Configuration config) : base("Flow Editor###ExComboEditor") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
        _config = config;
    }

    public void SetFlow(ComboFlow flow) {
        _flow         = flow;
        _canvasOffset = Vector2.Zero;
        _selectedNodeIds.Clear();
        WindowName    = $"Flow Editor — {flow.Name}###ExComboEditor";
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        if (_flow == null) { ImGui.TextDisabled("No flow selected."); return; }

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

        var mouse2 = ImGui.GetMousePos();

        // ── Edges ─────────────────────────────────────────────────────────
        FlowEdge? edgeToDelete = null;
        foreach (var edge in _flow.Edges) {
            var fn = _flow.Nodes.Find(n => n.Id == edge.FromNodeId);
            var tn = _flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if (fn == null || tn == null) continue;

            Vector2 p1;
            if (fn.Type is NodeType.Branch or NodeType.Condition) {
                var slotY = fn.Y + (edge.FromPortIndex + 0.5f) * BranchSlotH;
                p1 = canvasMin + _canvasOffset + new Vector2(fn.X + NodeSize.X, slotY);
            } else {
                p1 = canvasMin + _canvasOffset + new Vector2(fn.X + NodeSize.X, fn.Y + NodeSize.Y * 0.5f);
            }
            var p4  = canvasMin + _canvasOffset + new Vector2(tn.X, tn.Y + NodeHeight(tn) * 0.5f);
            var cp1 = p1 + new Vector2(60, 0);
            var cp2 = p4 - new Vector2(60, 0);
            dl.AddBezierCubic(p1, cp1, cp2, p4, Col(0.4f, 0.6f, 1f, 0.85f), 2f);

            // ── Delete button at midpoint ─────────────────────────────────
            var mid     = 0.125f*p1 + 0.375f*cp1 + 0.375f*cp2 + 0.125f*p4;
            const float Br = 7f;
            const float Bx = 3.5f;
            var btnHovered = _wireFromNodeId == null && Vector2.Distance(mouse2, mid) < Br;
            dl.AddCircleFilled(mid, Br, btnHovered ? Col(0.75f, 0.15f, 0.15f) : Col(0.18f, 0.18f, 0.22f));
            dl.AddCircle(mid, Br, btnHovered ? Col(1f, 0.4f, 0.4f) : Col(0.45f, 0.35f, 0.35f), 12, 1.5f);
            dl.AddLine(mid + new Vector2(-Bx, -Bx), mid + new Vector2(Bx, Bx), Col(1f, 1f, 1f, 0.9f), 1.5f);
            dl.AddLine(mid + new Vector2(Bx, -Bx), mid + new Vector2(-Bx, Bx), Col(1f, 1f, 1f, 0.9f), 1.5f);
            if (btnHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) edgeToDelete = edge;
        }
        if (edgeToDelete != null) {
            _flow.Edges.Remove(edgeToDelete);
            FlowExecutor.InvalidateFlow(_flow.Id);
            _config.Save();
        }

        // ── Pending wire ──────────────────────────────────────────────────
        if (_wireFromNodeId != null) {
            var wireMouse = ImGui.GetMousePos();
            var wfn       = _flow.Nodes.Find(n => n.Id == _wireFromNodeId);
            if (wfn != null) {
                Vector2 p1;
                if (wfn.Type is NodeType.Branch or NodeType.Condition) {
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
                dl.AddBezierCubic(p1, p1 + new Vector2(60, 0), wireEnd - new Vector2(60, 0), wireEnd,
                    Col(0.4f, 0.6f, 1f, 0.5f), 2f);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
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
                            _config.Save();
                        }
                        break;
                    }
                }
                _wireFromNodeId = null;
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
                var nx = n.X + NodeSize.X; var ny = n.Y + NodeHeight(n);
                if (nx > ex2) ex2 = nx;   if (ny > ey2) ey2 = ny;
                any = true;
            }
            if (any) {
                const float PadH = 6f;
                const float PadTop = 20f;
                var eMin = canvasMin + _canvasOffset + new Vector2(ex - PadH, ey - PadTop);
                var eMax = canvasMin + _canvasOffset + new Vector2(ex2 + PadH, ey2 + PadH);
                dl.AddRectFilled(eMin, eMax, Col(0.3f, 0.6f, 1f, 0.2f), 6f);
                DrawDashedRect(dl, eMin, eMax, Col(0.3f, 0.6f, 1f, 0.9f), 1.5f, 6f, 4f, 6f);
            }
        }

        // ── Nodes ─────────────────────────────────────────────────────────
        var anyNodeRightClicked = false;

        foreach (var node in _flow.Nodes) {
            var isTrigger   = node.Type == NodeType.Trigger;
            var isBranch    = node.Type == NodeType.Branch;
            var isCondition = node.Type == NodeType.Condition;
            var nodeH       = NodeHeight(node);
            var sp          = canvasMin + _canvasOffset + new Vector2(node.X, node.Y);
            var inPort      = sp + new Vector2(0f, nodeH * 0.5f);

            // ── Output port hover detection ───────────────────────────────
            bool overOutPort    = false;
            int  overOutPortIdx = 0;
            if (isBranch || isCondition) {
                if (_wireFromNodeId == null) {
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
                overOutPort = _wireFromNodeId == null && Vector2.Distance(mouse2, outPort) < PortRadius * 2f;
            }

            ImGui.SetCursorScreenPos(sp);
            ImGui.InvisibleButton($"node_{node.Id}", new Vector2(NodeSize.X, nodeH));
            var nodeHovered = ImGui.IsItemHovered();
            var nodeActive  = ImGui.IsItemActive();

            // ── Select on click ───────────────────────────────────────────
            if (nodeHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !overOutPort) {
                var xBtn = sp + new Vector2(NodeSize.X - 7f, 7f);
                if (Vector2.Distance(mouse2, xBtn) >= 7f) {
                    if (ImGui.GetIO().KeyCtrl)
                        { if (!_selectedNodeIds.Remove(node.Id)) _selectedNodeIds.Add(node.Id); }
                    else if (!_selectedNodeIds.Contains(node.Id))
                        { _selectedNodeIds.Clear(); _selectedNodeIds.Add(node.Id); }
                }
            }

            // ── Drag ──────────────────────────────────────────────────────
            if (nodeActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _wireFromNodeId != node.Id) {
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
                            sn.X = MathF.Round(sn.X / GridStep) * GridStep;
                            sn.Y = MathF.Round(sn.Y / GridStep) * GridStep;
                        }
                    }
                } else {
                    node.X = MathF.Round(node.X / GridStep) * GridStep;
                    node.Y = MathF.Round(node.Y / GridStep) * GridStep;
                }
                _draggingNodeId = null;
                _config.Save();
            }

            // ── Wire start ────────────────────────────────────────────────
            if (overOutPort && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) {
                _wireFromNodeId    = node.Id;
                _wireFromPortIndex = (isBranch || isCondition) ? overOutPortIdx : 0;
            }

            if (nodeHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) {
                if (isBranch)       OpenBranchEdit(node.Id, node.OutputCount);
                else if (isCondition) OpenConditionEdit(node.Id);
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
            if (isBranch) {
                var borderCol = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? Col(0.70f, 0.40f, 1.00f)
                        : Col(0.70f, 0.40f, 1.00f, 0.5f);
                dl.AddRectFilled(sp, sp + new Vector2(NodeSize.X, nodeH), Col(0.08f, 0.05f, 0.12f), 6f);
                dl.AddRect(sp, sp + new Vector2(NodeSize.X, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                var label      = "Branch";
                var labelWidth = ImGui.CalcTextSize(label).X;
                var labelPos   = sp + new Vector2((NodeSize.X - labelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, labelPos, label, Col(0.70f, 0.40f, 1.00f), true);

                // Input port (left midpoint)
                var overInPort = _wireFromNodeId != null && _wireFromNodeId != node.Id
                    && Vector2.Distance(mouse2, inPort) < PortRadius * 3f;
                dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.3f, 0.9f, 0.3f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.4f, 1f, 0.4f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                // Output ports
                for (var p = 0; p < node.OutputCount; p++) {
                    var portPos     = sp + new Vector2(NodeSize.X, (p + 0.5f) * BranchSlotH);
                    var portHovered = overOutPort && overOutPortIdx == p;
                    dl.AddCircleFilled(portPos, PortRadius,
                        portHovered ? Col(0.6f, 0.8f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(portPos, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                    // Port label (1-based) to the left of port
                    var numLabel = (p + 1).ToString();
                    var numW     = ImGui.CalcTextSize(numLabel).X;
                    DrawHelpers.DrawText(dl, portPos + new Vector2(-numW - PortRadius - 4f, -7f),
                        numLabel, Col(0.70f, 0.40f, 1.00f, 0.8f), false);

                    // Divider line between slots (except after last)
                    if (p < node.OutputCount - 1) {
                        var lineY = sp.Y + (p + 1) * BranchSlotH;
                        dl.AddLine(new Vector2(sp.X + 4f, lineY), new Vector2(sp.X + NodeSize.X - 4f, lineY),
                            Col(0.70f, 0.40f, 1.00f, 0.2f), 1f);
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
            } else if (isCondition) {
                // ── Condition node draw ───────────────────────────────────
                var condAccent = Col(0.90f, 0.63f, 0.31f);
                var borderCol  = isSelected
                    ? Col(1f, 1f, 1f)
                    : nodeHovered || nodeActive
                        ? condAccent
                        : Col(0.90f, 0.63f, 0.31f, 0.5f);
                dl.AddRectFilled(sp, sp + new Vector2(NodeSize.X, nodeH), Col(0.12f, 0.08f, 0.03f), 6f);

                // Job icon (dimmed, fills the node body)
                var jobIconId = GetJobIconId(_flow!.Job);
                if (jobIconId != 0) {
                    var tex = Plugin.TextureProvider
                        .GetFromGameIcon(new GameIconLookup(jobIconId))?.GetWrapOrDefault();
                    if (tex != null)
                        DrawHelpers.DrawIcon(dl, tex, sp, new Vector2(NodeSize.X, nodeH), 1f);
                }

                dl.AddRect(sp, sp + new Vector2(NodeSize.X, nodeH), borderCol, 6f, ImDrawFlags.None,
                    isSelected || nodeHovered ? 2f : 1.5f);

                var condLabel      = node.ConditionField != ""
                    ? $"{node.ConditionField} {((CompareOp)node.ConditionCompareOp).ToLabel()} {node.ConditionCompareVal}"
                    : "Job Condition";
                var condLabelWidth = ImGui.CalcTextSize(condLabel).X;
                var condLabelPos   = sp + new Vector2((NodeSize.X - condLabelWidth) * 0.5f, -16f);
                DrawHelpers.DrawText(dl, condLabelPos, condLabel, condAccent, true);

                // Input port
                var overInPort = _wireFromNodeId != null && _wireFromNodeId != node.Id
                    && Vector2.Distance(mouse2, inPort) < PortRadius * 3f;
                dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.3f, 0.9f, 0.3f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.4f, 1f, 0.4f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                // Output ports: port 0 = T (green), port 1 = F (red)
                for (var p = 0; p < node.OutputCount; p++) {
                    var portPos     = sp + new Vector2(NodeSize.X, (p + 0.5f) * BranchSlotH);
                    var portHovered = overOutPort && overOutPortIdx == p;
                    dl.AddCircleFilled(portPos, PortRadius,
                        portHovered ? Col(0.6f, 0.8f, 1f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(portPos, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

                    var portLabel = p == 0 ? "T" : "F";
                    var portLabelCol = p == 0 ? Col(0.35f, 0.90f, 0.35f, 0.9f) : Col(0.90f, 0.35f, 0.35f, 0.9f);
                    var plW = ImGui.CalcTextSize(portLabel).X;
                    DrawHelpers.DrawText(dl, portPos + new Vector2(-plW - PortRadius - 4f, -7f),
                        portLabel, portLabelCol, false);

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
                var accentR   = isTrigger ? 0.635f : 0.455f;
                var accentG   = isTrigger ? 0.855f : 0.765f;
                var accentB   = isTrigger ? 0.549f : 1.000f;
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
                        DrawHelpers.DrawIcon(dl, tex, sp, NodeSize, 1f);
                }

                dl.AddRect(sp, sp + NodeSize, borderCol, 6f, ImDrawFlags.None, isSelected || nodeHovered ? 2f : 1.5f);

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
                    var overInPort = _wireFromNodeId != null && _wireFromNodeId != node.Id
                        && Vector2.Distance(mouse2, inPort) < PortRadius * 3f;
                    dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.3f, 0.9f, 0.3f) : Col(0.25f, 0.25f, 0.35f));
                    dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.4f, 1f, 0.4f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
                }
                dl.AddCircleFilled(outPort, PortRadius, overOutPort ? Col(0.6f, 0.8f, 1f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(outPort, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
            }

            // ── Context menu ──────────────────────────────────────────────
            if (ImGui.BeginPopup($"node_ctx_{node.Id}")) {
                if (isCondition) {
                    if (IconMenuItem(FontAwesomeIcon.Filter, "Edit Job Condition")) OpenConditionEdit(node.Id);
                } else if (isBranch) {
                    if (IconMenuItem(FontAwesomeIcon.List,   "Edit Outputs")) OpenBranchEdit(node.Id, node.OutputCount);
                } else {
                    if (IconMenuItem(FontAwesomeIcon.Edit,   "Edit Action"))  OpenPicker(node.Id);
                }
                ImGui.Separator();
                if (IconMenuItem(FontAwesomeIcon.Copy, "Copy")) {
                    _clipboardNodes = [(node.Id, node.Type, 0f, 0f, node.ActionId, node.ActionLabel, node.IconId, node.OutputCount, node.ConditionField, node.ConditionCompareOp, (int)node.ConditionCompareVal, node.IsOgcd)];
                    _clipboardEdges = [];
                }
                if (IconMenuItem(FontAwesomeIcon.TrashAlt, "Delete Node"))  _pendingDeleteNodeId = node.Id;
                if (IconMenuItem(FontAwesomeIcon.Unlink,   "Remove Links")) {
                    _flow.Edges.RemoveAll(e => e.FromNodeId == node.Id || e.ToNodeId == node.Id);
                    FlowExecutor.InvalidateFlow(_flow.Id);
                    _config.Save();
                }
                ImGui.EndPopup();
            }
        }

        // ── Multi-selection context menu ──────────────────────────────────
        if (ImGui.BeginPopup("##multi_node_ctx")) {
            var selCount = _selectedNodeIds.Count;
            if (IconMenuItem(FontAwesomeIcon.TrashAlt, $"Delete {selCount} nodes")) {
                foreach (var id in _selectedNodeIds) {
                    _flow.Edges.RemoveAll(e => e.FromNodeId == id || e.ToNodeId == id);
                    _flow.Nodes.RemoveAll(n => n.Id == id);
                }
                _selectedNodeIds.Clear();
                FlowExecutor.InvalidateFlow(_flow.Id);
                _config.Save();
                ImGui.CloseCurrentPopup();
            }
            if (IconMenuItem(FontAwesomeIcon.Copy, "Copy")) {
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
                    _clipboardNodes.Add((n.Id, n.Type, n.X - cx, n.Y - cy, n.ActionId, n.ActionLabel, n.IconId, n.OutputCount, n.ConditionField, n.ConditionCompareOp, (int)n.ConditionCompareVal, n.IsOgcd));
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
            dl.AddRectFilled(rMin, rMax, Col(0.3f, 0.6f, 1f, 0.2f), 6f);
            dl.AddRect(rMin, rMax, Col(0.3f, 0.6f, 1f, 0.9f), 6f, ImDrawFlags.None, 1.5f);
        }

        // ── Pending deletes (outside node loop) ───────────────────────────
        if (_pendingDeleteNodeId != null) {
            _selectedNodeIds.Remove(_pendingDeleteNodeId);
            _flow.Edges.RemoveAll(e => e.FromNodeId == _pendingDeleteNodeId || e.ToNodeId == _pendingDeleteNodeId);
            _flow.Nodes.RemoveAll(n => n.Id == _pendingDeleteNodeId);
            FlowExecutor.InvalidateFlow(_flow.Id);
            _config.Save();
            _pendingDeleteNodeId = null;
        }

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
            && !_isMarqueeSelecting && _wireFromNodeId == null) {
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
                var nMax = nsp + new Vector2(NodeSize.X, NodeHeight(n));
                if (nsp.X < rMax.X && nMax.X > rMin.X && nsp.Y < rMax.Y && nMax.Y > rMin.Y)
                    _selectedNodeIds.Add(n.Id);
            }
        }

        if (!anyNodeRightClicked && canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            _contextMenuCanvasPos = mouse2 - canvasMin - _canvasOffset;
            ImGui.OpenPopup("##canvas_ctx");
        }
        if (ImGui.BeginPopup("##canvas_ctx")) {
            if (IconMenuItem(FontAwesomeIcon.Bolt,        "Add Trigger"))   AddNode(NodeType.Trigger);
            if (IconMenuItem(FontAwesomeIcon.Magic,       "Add Action"))    AddNode(NodeType.Action);
            if (IconMenuItem(FontAwesomeIcon.CodeBranch,  "Add Branch"))    AddNode(NodeType.Branch);
            if (IconMenuItem(FontAwesomeIcon.Filter,      "Add Job Condition")) AddConditionNode();
            if (_clipboardNodes != null) {
                ImGui.Separator();
                if (IconMenuItem(FontAwesomeIcon.Paste, $"Paste ({_clipboardNodes.Count} nodes)")) {
                    var pastePos = _contextMenuCanvasPos;
                    var idMap    = new Dictionary<string, string>();
                    var newNodes = new List<FlowNode>();
                    foreach (var (origId, type, relX, relY, actionId, label, iconId, outputCount, condField, condOp, condVal, isOgcd) in _clipboardNodes) {
                        var nn = new FlowNode {
                            Type               = type,
                            X                  = MathF.Round((pastePos.X + relX) / GridStep) * GridStep,
                            Y                  = MathF.Round((pastePos.Y + relY) / GridStep) * GridStep,
                            ActionId           = actionId,
                            ActionLabel        = label,
                            IconId             = iconId,
                            OutputCount        = outputCount,
                            ConditionField     = condField,
                            ConditionCompareOp = condOp,
                            ConditionCompareVal= condVal,
                            IsOgcd             = isOgcd,
                        };
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
                    _config.Save();
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
        if (_pendingOpenBranchEdit) { ImGui.OpenPopup("Branch Outputs##branchedit"); _pendingOpenBranchEdit = false; }
        if (_pendingOpenCondEdit)   { ImGui.OpenPopup("Edit Condition##condedit");   _pendingOpenCondEdit   = false; }

        // ── Modals ────────────────────────────────────────────────────────
        DrawActionPicker();
        DrawBranchEdit();
        DrawConditionEdit();
    }

    private void AddNode(NodeType type) {
        var node = new FlowNode {
            Type = type,
            X    = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y    = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        _config.Save();
        if (type != NodeType.Branch && type != NodeType.Condition)
            OpenPicker(node.Id);
    }

    private void AddConditionNode() {
        var node = new FlowNode {
            Type        = NodeType.Condition,
            OutputCount = 2,
            X           = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y           = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        _config.Save();
        OpenConditionEdit(node.Id);
    }

    private void OpenBranchEdit(string nodeId, int currentCount) {
        _branchEditNodeId    = nodeId;
        _branchEditCount     = currentCount;
        _pendingOpenBranchEdit = true;
    }

    private void DrawBranchEdit() {
        if (_branchEditNodeId == null) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 110), new Vector2(500, 200));
        ImGui.SetNextWindowSize(new Vector2(280, 120), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Branch Outputs##branchedit", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) return;

        ImGui.TextDisabled("Output count");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputInt("##outcount", ref _branchEditCount);
        if (_branchEditCount < 2)  _branchEditCount = 2;
        if (_branchEditCount > 16) _branchEditCount = 16;

        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.455f, 0.765f, 1.000f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.592f, 0.831f, 1.000f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.350f, 0.650f, 0.900f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.102f, 0.106f, 0.118f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _branchEditNodeId);
            if (node != null) {
                for (var p = _branchEditCount; p < node.OutputCount; p++)
                    _flow.Edges.RemoveAll(e => e.FromNodeId == node.Id && e.FromPortIndex == p);
                node.OutputCount = _branchEditCount;
                FlowExecutor.InvalidateFlow(_flow.Id);
                _config.Save();
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
                        var aRow = Plugin.DataManager.GetExcelSheet<LuminaAction>()?.GetRow(id);
                        node.IsOgcd      = aRow.HasValue && aRow.Value.CooldownGroup != 57;
                        FlowExecutor.InvalidateFlow(_flow.Id);
                        _config.Save();
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
        var accent    = new Vector4(0.455f, 0.765f, 1.000f, 1f);
        var accentHov = new Vector4(0.592f, 0.831f, 1.000f, 1f);
        var accentAct = new Vector4(0.350f, 0.650f, 0.900f, 1f);
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

    private void BuildJobCategorySet() {
        _pickerJobCategoryIds = null;
        var job = _flow?.Job;
        if (string.IsNullOrEmpty(job)) return;
        var catSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJobCategory>();
        if (catSheet == null) return;
        _pickerJobCategoryIds = new HashSet<uint>();
        foreach (var cat in catSheet)
            if (CategoryHasJob(cat, job))
                _pickerJobCategoryIds.Add(cat.RowId);
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

        foreach (var row in sheet) {
            if (!row.IsPlayerAction && !row.IsPvP) continue;
            var name = row.Name.ToString();
            if (name == "") continue;
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (_pickerJobCategoryIds != null && !_pickerJobCategoryIds.Contains(row.ClassJobCategory.RowId)) continue;
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
        var node           = _flow!.Nodes.Find(n => n.Id == nodeId);
        _condEditNodeId    = nodeId;
        _condFieldSearch   = "";
        _condFieldSelected = node?.ConditionField ?? "";
        _condEditOp        = node?.ConditionCompareOp ?? 5;
        _condEditVal       = (int)(node?.ConditionCompareVal ?? 1f);
        _pendingOpenCondEdit = true;
    }

    private void DrawConditionEdit() {
        if (_condEditNodeId == null) return;

        ImGui.SetNextWindowSizeConstraints(new Vector2(320, 300), new Vector2(600, 500));
        ImGui.SetNextWindowSize(new Vector2(360, 380), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Edit Condition##condedit",
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) return;

        var fields = JobGaugeRegistry.GetFields(_flow?.Job ?? "");

        ImGui.TextDisabled("Field");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##cfsearch", ref _condFieldSearch, 64);

        ImGui.BeginChild("##cffield", new Vector2(0, -110f), true);
        if (fields != null) {
            foreach (var f in fields) {
                if (_condFieldSearch.Length > 0
                    && !f.Name.Contains(_condFieldSearch, StringComparison.OrdinalIgnoreCase)) continue;
                bool sel = f.Name == _condFieldSelected;
                if (ImGui.Selectable(f.Name, sel))
                    _condFieldSelected = f.Name;
            }
        } else {
            ImGui.TextDisabled("No gauge fields for this job.");
        }
        ImGui.EndChild();

        ImGui.Spacing();

        string[] opLabels = ["==", "!=", "<", "≤", ">", "≥"];
        ImGui.TextDisabled("Compare");
        ImGui.SetNextItemWidth(80f);
        ImGui.Combo("##cfop", ref _condEditOp, opLabels, opLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        ImGui.DragInt("##cfval", ref _condEditVal, 1f);

        ImGui.Spacing();

        var btnW = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.90f, 0.63f, 0.31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.00f, 0.75f, 0.45f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.75f, 0.50f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.10f, 0.06f, 0.02f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 5f));
        if (ImGui.Button("OK", new Vector2(btnW, 0f))) {
            var node = _flow!.Nodes.Find(n => n.Id == _condEditNodeId);
            if (node != null) {
                node.ConditionField      = _condFieldSelected;
                node.ConditionCompareOp  = _condEditOp;
                node.ConditionCompareVal = _condEditVal;
                FlowExecutor.InvalidateFlow(_flow.Id);
                _config.Save();
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

        ImGui.EndPopup();
    }
}
