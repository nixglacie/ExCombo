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
        // Keybind/Toggle have their own small modals; everything else uses the condition editor.
        if      (type == NodeType.KeybindCondition) OpenKeybindEdit(node.Id);
        else if (type == NodeType.ToggleCondition)  OpenToggleEdit(node.Id);
        else                                        OpenConditionEdit(node.Id);
    }

    private void AddLatchNode() {
        var node = new FlowNode {
            Type        = NodeType.LatchCondition,
            OutputCount = 2,
            X           = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y           = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        TryConnectDropped(node);
        Commit();
    }

    private void AddLogicNode() {
        var node = new FlowNode {
            Type            = NodeType.LogicCondition,
            OutputCount     = 2,
            LogicInputCount = 2,
            LogicExpr       = "1 AND 2",
            X               = MathF.Round((_contextMenuCanvasPos.X - NodeSize.X * 0.5f) / GridStep) * GridStep,
            Y               = MathF.Round((_contextMenuCanvasPos.Y - NodeSize.Y * 0.5f) / GridStep) * GridStep,
        };
        _flow!.Nodes.Add(node);
        TryConnectDropped(node);
        Commit();
        OpenLogicEdit(node.Id);
    }

    // Wire a node just created from the drop-menu into the dangling connection, if any.
    private void TryConnectDropped(FlowNode nn) {
        if (_dropSrcNodeId != null) {
            if (nn.Type != NodeType.Trigger && nn.Type != NodeType.Note) {
                _flow!.Edges.RemoveAll(e => e.FromNodeId == _dropSrcNodeId && e.FromPortIndex == _dropSrcPort && e.ToPortIndex == 0);
                _flow.Edges.Add(new FlowEdge { FromNodeId = _dropSrcNodeId, ToNodeId = nn.Id, FromPortIndex = _dropSrcPort });
                FlowExecutor.InvalidateFlow(_flow.Id);
            }
            _dropSrcNodeId = null;
        }
        if (_dropDstNodeId != null) {
            // Predicate slots (>= 1) only accept condition/Logic sources; flow slot takes anything but Notes.
            var okSrc = _dropDstPort == 0 || FlowNode.IsGate(nn.Type);
            if (nn.Type != NodeType.Note && nn.Id != _dropDstNodeId && okSrc) {
                if (_dropDstPort == 0)
                    _flow!.Edges.RemoveAll(e => e.FromNodeId == nn.Id && e.FromPortIndex == 0 && e.ToPortIndex == 0);
                else
                    _flow!.Edges.RemoveAll(e => e.ToNodeId == _dropDstNodeId && e.ToPortIndex == _dropDstPort);
                _flow.Edges.Add(new FlowEdge {
                    FromNodeId = nn.Id, ToNodeId = _dropDstNodeId,
                    FromPortIndex = 0, ToPortIndex = _dropDstPort,
                });
                FlowExecutor.InvalidateFlow(_flow.Id);
            }
            _dropDstNodeId = null;
            _dropDstPort   = 0;
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
        NodeType.ActionHistoryCondition => "Action",
        NodeType.GaugeCondition    => "Gauge",
        NodeType.LogicCondition    => "Logic",
        NodeType.KeybindCondition  => "Keybind",
        NodeType.ToggleCondition   => "Toggle",
        NodeType.LatchCondition    => "Latch",
        _ => "Condition",
    };

    // Display name of the Keybind gate's chosen key.
    internal static string KeyName(uint vk) => vk switch {
        16 => "Shift", 17 => "Ctrl", 18 => "Alt",
        0  => "Keybind",
        _  => ((Dalamud.Game.ClientState.Keys.VirtualKey)vk).ToString(),
    };

    private static string GateNodeLabel(FlowNode n) {
        if (n.Type == NodeType.LogicCondition)
            return n.LogicExpr is "" ? "Logic" : n.LogicExpr;
        if (n.Type == NodeType.KeybindCondition)
            return n.CheckParamId == 0 ? "Keybind" : $"Hold {KeyName(n.CheckParamId)}";
        if (n.Type == NodeType.ToggleCondition)
            return n.ActionLabel is "" ? "Toggle" : n.ActionLabel;
        if (n.Type == NodeType.LatchCondition)
            return "Latch";
        var op  = ((CompareOp)n.ConditionCompareOp).ToLabel();
        if (n.Type == NodeType.Condition)
            return n.ConditionField != "" ? $"{n.ConditionField} {op} {n.ConditionCompareVal}" : "Job Condition";
        if (n.Type == NodeType.GaugeCondition)
            return n.CheckField != "" ? $"{n.CheckField} {op} {n.ConditionCompareVal:0.##}" : "Gauge";
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
}
