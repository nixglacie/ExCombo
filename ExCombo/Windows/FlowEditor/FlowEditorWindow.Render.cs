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
    private void DrawEdges(ImDrawListPtr dl, Vector2 canvasMin, Vector2 mouse2, bool inspect) {
        if (_flow == null) return;
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
            var p4  = InputPortPos(tn, edge.ToPortIndex, canvasMin);
            // Gate (condition) edges are colored by branch: port 0 = true (green), port 1 = false (red).
            // When the inspector is on, edges instead carry the downstream node's live state.
            uint  edgeCol;
            float thick = edge.ToPortIndex > 0 ? 1.6f : 2f;   // predicate wires read thinner
            if (inspect) {
                edgeCol = edge.ToPortIndex > 0
                    // Predicate wire: colored by the live signal it carries into the Logic input.
                    ? (FlowExecutor.PredicateSignal(_flow, fn, edge.FromPortIndex)
                        ? Col(0.30f, 0.85f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f))
                    : InspectEdgeColor(_flow, fn, tn, edge);
                if (tn.Type == NodeType.Action && FlowExecutor.IsQueuedAction(_flow, tn.Id)) thick = 2.75f; // match node gold pulse weight
            } else {
                edgeCol = FlowNode.IsGate(fn.Type)
                    ? (edge.FromPortIndex == 0 ? Col(0.30f, 0.80f, 0.30f, 0.9f) : Col(0.85f, 0.30f, 0.30f, 0.9f))
                    : Col(0.4f, 0.6f, 1f, 0.85f);
            }
            DrawWire(dl, p1, p4, edgeCol, thick);

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
                        else         { _wireToNodeId   = edge.ToNodeId; _wireToPortIndex = edge.ToPortIndex; }
                    }
                }
            }
        }
        if (edgeToDelete != null) {
            _flow.Edges.Remove(edgeToDelete);
            FlowExecutor.InvalidateFlow(_flow.Id);
            Commit();
        }
    }

    private void DrawPendingWires(ImDrawListPtr dl, Vector2 canvasMin, Vector2 mouse2) {
        if (_flow == null) return;
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
                    if (FindInputSlotAt(t, wireMouse, canvasMin, bodyAsFlowInput: true) is { } snapSlot) {
                        wireEnd = InputPortPos(t, snapSlot, canvasMin);
                        break;
                    }
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
                    if (FindInputSlotAt(t, wireMouse, canvasMin, bodyAsFlowInput: true) is not { } slot) continue;

                    // Predicate slots (>= 1) only accept condition/Logic outputs; other drops are swallowed.
                    var srcNode = _flow.Nodes.Find(n => n.Id == _wireFromNodeId);
                    if (slot >= 1 && (srcNode == null || !FlowNode.IsGate(srcNode.Type))) {
                        connected = true;
                        break;
                    }
                    // prevent duplicate edge on same port/slot
                    if (!_flow.Edges.Exists(e => e.FromNodeId == _wireFromNodeId
                                               && e.FromPortIndex == _wireFromPortIndex
                                               && e.ToNodeId == t.Id
                                               && e.ToPortIndex == slot)) {
                        if (slot == 0)
                            // Flow edges: one outgoing flow edge per output port (predicate
                            // fan-outs from the same port survive).
                            _flow.Edges.RemoveAll(e => e.FromNodeId == _wireFromNodeId
                                                    && e.FromPortIndex == _wireFromPortIndex
                                                    && e.ToPortIndex == 0);
                        else
                            // Predicate edges: one wire per Logic input slot.
                            _flow.Edges.RemoveAll(e => e.ToNodeId == t.Id && e.ToPortIndex == slot);
                        _flow.Edges.Add(new FlowEdge {
                            FromNodeId    = _wireFromNodeId,
                            ToNodeId      = t.Id,
                            FromPortIndex = _wireFromPortIndex,
                            ToPortIndex   = slot,
                        });
                        FlowExecutor.InvalidateFlow(_flow.Id);
                        Commit();
                    }
                    connected = true;
                    break;
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
                var p4b   = InputPortPos(ttn, _wireToPortIndex, canvasMin);
                var hit   = FindOutputPortAt(mouse2, canvasMin);
                var start = hit is { } h && h.NodeId != _wireToNodeId
                    ? OutputPortPos(_flow.Nodes.Find(n => n.Id == h.NodeId)!, h.Port, canvasMin)
                    : mouse2;
                DrawWire(dl, start, p4b, Col(0.4f, 0.6f, 1f, 0.5f), 2f);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) {
                    if (hit is { } hh && hh.NodeId != _wireToNodeId) {
                        // Predicate slots only accept condition/Logic outputs.
                        var srcNode = _flow.Nodes.Find(n => n.Id == hh.NodeId);
                        var okSrc   = _wireToPortIndex == 0 || (srcNode != null && FlowNode.IsGate(srcNode.Type));
                        if (okSrc && !_flow.Edges.Exists(e => e.FromNodeId == hh.NodeId && e.FromPortIndex == hh.Port
                                                           && e.ToNodeId == _wireToNodeId && e.ToPortIndex == _wireToPortIndex)) {
                            if (_wireToPortIndex == 0)
                                _flow.Edges.RemoveAll(e => e.FromNodeId == hh.NodeId && e.FromPortIndex == hh.Port && e.ToPortIndex == 0);
                            else
                                _flow.Edges.RemoveAll(e => e.ToNodeId == _wireToNodeId && e.ToPortIndex == _wireToPortIndex);
                            _flow.Edges.Add(new FlowEdge {
                                FromNodeId = hh.NodeId, ToNodeId = _wireToNodeId,
                                FromPortIndex = hh.Port, ToPortIndex = _wireToPortIndex,
                            });
                            FlowExecutor.InvalidateFlow(_flow.Id);
                            Commit();
                        }
                    } else {
                        // Dropped on empty space → add-node menu; the new node wires as the source.
                        _dropDstNodeId        = _wireToNodeId;
                        _dropDstPort          = _wireToPortIndex;
                        _contextMenuCanvasPos = mouse2 - canvasMin - _canvasOffset;
                        ImGui.OpenPopup("##canvas_ctx");
                    }
                    _wireToNodeId    = null;
                    _wireToPortIndex = 0;
                }
            }
        }
    }

    private void DrawGroupBoxes(ImDrawListPtr dl, Vector2 canvasMin) {
        if (_flow == null) return;
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
    }

    private void DrawSelectionEnvelope(ImDrawListPtr dl, Vector2 canvasMin) {
        if (_flow == null) return;
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
                DrawHelpers.DrawDashedRect(dl, eMin, eMax, Style.AccentU32(0.9f), 1.5f, 6f, 4f, 6f);
            }
        }
    }
}
