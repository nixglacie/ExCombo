using System.Collections.Generic;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static class FlowExecutor {
    public static unsafe uint Resolve(ComboFlow flow, uint triggerActionId) {
        var chain = GetChain(flow);
        if (chain.Count == 0) return triggerActionId;

        var lastCombo = ActionManager.Instance()->Combo.Action;
        for (var i = chain.Count - 1; i >= 1; i--)
            if (lastCombo == chain[i - 1].ActionId)
                return chain[i].ActionId;
        return chain[0].ActionId;
    }

    private static List<FlowNode> GetChain(ComboFlow flow) {
        var trigger = flow.Nodes.Find(n => n.Type == NodeType.Trigger);
        if (trigger == null) return [];

        var chain   = new List<FlowNode>();
        var current = trigger.Id;
        var visited = new HashSet<string>();

        while (!visited.Contains(current)) {
            visited.Add(current);
            var edge = flow.Edges.Find(e => e.FromNodeId == current);
            if (edge == null) break;
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if (next is not { Type: NodeType.Action }) break;
            chain.Add(next);
            current = next.Id;
        }
        return chain;
    }
}
