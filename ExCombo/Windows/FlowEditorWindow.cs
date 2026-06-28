using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private string? _pickerNodeId;
    private string  _pickerSearch     = "";
    private string  _pickerLastSearch = "\0";
    private readonly List<(uint Id, string Name, uint Icon)> _pickerResults = new();
    private Vector2 _contextMenuCanvasPos;
    private string? _pendingDeleteNodeId;
    private string? _draggingNodeId;

    private static readonly Vector2 NodeSize  = new(64f, 64f);
    private const           float   PortRadius = 6f;
    private const           float   GridStep   = 32f;

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
            var p1  = canvasMin + _canvasOffset + new Vector2(fn.X + NodeSize.X, fn.Y + NodeSize.Y * 0.5f);
            var p4  = canvasMin + _canvasOffset + new Vector2(tn.X,              tn.Y + NodeSize.Y * 0.5f);
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
        if (edgeToDelete != null) { _flow.Edges.Remove(edgeToDelete); _config.Save(); }

        // ── Pending wire ──────────────────────────────────────────────────
        if (_wireFromNodeId != null) {
            var wireMouse = ImGui.GetMousePos();
            var wfn       = _flow.Nodes.Find(n => n.Id == _wireFromNodeId);
            if (wfn != null) {
                var p1      = canvasMin + _canvasOffset + new Vector2(wfn.X + NodeSize.X, wfn.Y + NodeSize.Y * 0.5f);
                var wireEnd = wireMouse;
                foreach (var t in _flow.Nodes) {
                    if (t.Id == _wireFromNodeId) continue;
                    if (t.Type == NodeType.Trigger) continue;
                    var tip = canvasMin + _canvasOffset + new Vector2(t.X, t.Y + NodeSize.Y * 0.5f);
                    if (Vector2.Distance(wireMouse, tip) < PortRadius * 3f) { wireEnd = tip; break; }
                }
                dl.AddBezierCubic(p1, p1 + new Vector2(60, 0), wireEnd - new Vector2(60, 0), wireEnd,
                    Col(0.4f, 0.6f, 1f, 0.5f), 2f);
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                foreach (var t in _flow.Nodes) {
                    if (t.Id == _wireFromNodeId) continue;
                    if (t.Type == NodeType.Trigger) continue;
                    var tip = canvasMin + _canvasOffset + new Vector2(t.X, t.Y + NodeSize.Y * 0.5f);
                    if (Vector2.Distance(wireMouse, tip) < PortRadius * 3f) {
                        if (!_flow.Edges.Exists(e => e.FromNodeId == _wireFromNodeId && e.ToNodeId == t.Id))
                            _flow.Edges.Add(new FlowEdge { FromNodeId = _wireFromNodeId, ToNodeId = t.Id });
                        _config.Save();
                        break;
                    }
                }
                _wireFromNodeId = null;
            }
        }

        // ── Nodes ─────────────────────────────────────────────────────────

        foreach (var node in _flow.Nodes) {
            var sp      = canvasMin + _canvasOffset + new Vector2(node.X, node.Y);
            var outPort = sp + new Vector2(NodeSize.X, NodeSize.Y * 0.5f);
            var inPort  = sp + new Vector2(0f,         NodeSize.Y * 0.5f);
            var overOutPort = _wireFromNodeId == null
                && Vector2.Distance(mouse2, outPort) < PortRadius * 2f;

            ImGui.SetCursorScreenPos(sp);
            ImGui.InvisibleButton($"node_{node.Id}", NodeSize);
            var nodeHovered = ImGui.IsItemHovered();
            var nodeActive  = ImGui.IsItemActive();

            if (nodeActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _wireFromNodeId != node.Id) {
                _draggingNodeId = node.Id;
                var delta = ImGui.GetIO().MouseDelta;
                node.X += delta.X;
                node.Y += delta.Y;
            }
            if (ImGui.IsItemDeactivated() && _draggingNodeId == node.Id) {
                node.X = MathF.Round(node.X / GridStep) * GridStep;
                node.Y = MathF.Round(node.Y / GridStep) * GridStep;
                _draggingNodeId = null;
                _config.Save();
            }

            if (overOutPort && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _wireFromNodeId = node.Id;

            if (nodeHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                OpenPicker(node.Id);

            if (nodeHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup($"node_ctx_{node.Id}");

            // ── Draw node ─────────────────────────────────────────────────
            var isTrigger = node.Type == NodeType.Trigger;
            // --success #a2da8c (trigger), --accent #74c3ff (action)
            var accentR   = isTrigger ? 0.635f : 0.455f;
            var accentG   = isTrigger ? 0.855f : 0.765f;
            var accentB   = isTrigger ? 0.549f : 1.000f;
            var bgCol     = isTrigger ? Col(0.09f, 0.13f, 0.10f) : Col(0.09f, 0.11f, 0.16f);
            var borderCol = nodeHovered || nodeActive
                ? Col(accentR, accentG, accentB)
                : Col(accentR, accentG, accentB, 0.5f);
            dl.AddRectFilled(sp, sp + NodeSize, bgCol, 6f);

            if (node.IconId != 0) {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(node.IconId))?.GetWrapOrDefault();
                if (tex != null)
                    DrawHelpers.DrawIcon(dl, tex, sp, NodeSize, 1f);
            }

            dl.AddRect(sp, sp + NodeSize, borderCol, 6f, ImDrawFlags.None, nodeHovered ? 2f : 1.5f);

            var label      = node.ActionLabel != "" ? node.ActionLabel : (isTrigger ? "Trigger" : "Action");
            var labelWidth = ImGui.CalcTextSize(label).X;
            var labelPos   = sp + new Vector2((NodeSize.X - labelWidth) * 0.5f, -16f);
            DrawHelpers.DrawText(dl, labelPos, label, Col(accentR, accentG, accentB), true);

            // ── Delete button (top-right, visible on hover) ───────────────
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

            // ports
            if (!isTrigger) {
                var overInPort = _wireFromNodeId != null && _wireFromNodeId != node.Id
                    && Vector2.Distance(mouse2, inPort) < PortRadius * 3f;
                dl.AddCircleFilled(inPort, PortRadius, overInPort ? Col(0.3f, 0.9f, 0.3f) : Col(0.25f, 0.25f, 0.35f));
                dl.AddCircle(inPort, PortRadius, overInPort ? Col(0.4f, 1f, 0.4f) : Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
            }
            dl.AddCircleFilled(outPort, PortRadius, overOutPort ? Col(0.6f, 0.8f, 1f) : Col(0.25f, 0.25f, 0.35f));
            dl.AddCircle(outPort, PortRadius, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);

            // node context menu
            if (ImGui.BeginPopup($"node_ctx_{node.Id}")) {
                if (ImGui.MenuItem("Edit Action")) OpenPicker(node.Id);
                if (ImGui.MenuItem("Delete Node")) _pendingDeleteNodeId = node.Id;
                if (ImGui.MenuItem("Remove Wires")) {
                    _flow.Edges.RemoveAll(e => e.FromNodeId == node.Id || e.ToNodeId == node.Id);
                    _config.Save();
                }
                ImGui.EndPopup();
            }
        }

        // ── Pending deletes (outside node loop) ───────────────────────────
        if (_pendingDeleteNodeId != null) {
            _flow.Edges.RemoveAll(e => e.FromNodeId == _pendingDeleteNodeId || e.ToNodeId == _pendingDeleteNodeId);
            _flow.Nodes.RemoveAll(n => n.Id == _pendingDeleteNodeId);
            _config.Save();
            _pendingDeleteNodeId = null;
        }

        // ── Canvas input (submitted after nodes so nodes win HoveredId) ───
        ImGui.SetCursorScreenPos(canvasMin);
        ImGui.InvisibleButton("##canvas", canvasSize, ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) {
            _contextMenuCanvasPos = mouse2 - canvasMin - _canvasOffset;
            ImGui.OpenPopup("##canvas_ctx");
        }
        if (ImGui.BeginPopup("##canvas_ctx")) {
            if (ImGui.MenuItem("Add Trigger")) AddNode(NodeType.Trigger);
            if (ImGui.MenuItem("Add Action"))  AddNode(NodeType.Action);
            ImGui.EndPopup();
        }

        // ── Hint ──────────────────────────────────────────────────────────
        if (_flow.Nodes.Count == 0)
            dl.AddText(canvasMin + new Vector2(12f, 12f), Col(0.333f, 0.353f, 0.388f),
                "Right-click to add nodes  •  Middle-drag to pan  •  Drag output port (right circle) to wire");

        // ── Action picker modal ───────────────────────────────────────────
        DrawActionPicker();
    }

    private void AddNode(NodeType type) {
        var node = new FlowNode {
            Type = type,
            X    = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y    = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        _config.Save();
        OpenPicker(node.Id);
    }

    private void OpenPicker(string nodeId) {
        _pickerNodeId     = nodeId;
        _pickerSearch     = "";
        _pickerLastSearch = "\0";
        _pickerResults.Clear();
        ImGui.OpenPopup("Pick Action##picker");
    }

    private void DrawActionPicker() {
        if (_pickerNodeId == null) return;

        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.BeginPopupModal("Pick Action##picker")) return;

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##search", ref _pickerSearch, 256);

        if (_pickerSearch != _pickerLastSearch) {
            _pickerLastSearch = _pickerSearch;
            UpdatePickerResults();
        }

        if (_pickerSearch.Length < 2) {
            ImGui.TextDisabled("Type 2+ characters to search actions...");
        } else {
            ImGui.BeginChild("##results", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), true);
            foreach (var (id, name, icon) in _pickerResults) {
                if (icon != 0) {
                    var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon))?.GetWrapOrDefault();
                    if (tex != null) {
                        ImGui.Image(tex.Handle, new Vector2(20f, 20f));
                        ImGui.SameLine();
                    }
                }
                if (ImGui.Selectable($"{name}  (ID {id})##s{id}", false)) {
                    var node = _flow!.Nodes.Find(n => n.Id == _pickerNodeId);
                    if (node != null) {
                        node.ActionId    = id;
                        node.ActionLabel = name;
                        node.IconId      = icon;
                        _config.Save();
                    }
                    _pickerNodeId = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Cancel")) {
            _pickerNodeId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void UpdatePickerResults() {
        _pickerResults.Clear();
        var sheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
        if (sheet == null) return;

        var query = _pickerSearch.Trim();
        if (query.Length < 2) return;

        foreach (var row in sheet) {
            var name = row.Name.ToString();
            if (name == "") continue;
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            _pickerResults.Add((row.RowId, name, (uint)row.Icon));
            if (_pickerResults.Count >= 100) break;
        }
    }
}
