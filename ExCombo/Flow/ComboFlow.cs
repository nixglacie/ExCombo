using System;
using System.Collections.Generic;

namespace ExCombo.Flow;

public class ComboFlow {
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Flow";
    public string Job  { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<FlowNode> Nodes { get; set; } = new();
    public List<FlowEdge> Edges { get; set; } = new();
}
