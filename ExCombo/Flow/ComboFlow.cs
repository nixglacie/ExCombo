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

    // Editor canvas pan offset, persisted so reopening restores the view.
    public float ViewX { get; set; }
    public float ViewY { get; set; }

    // Per-flow tuning overrides. Null = inherit the global Configuration value.
    public int?   MaxWeavesPerGcd   { get; set; }
    public float? AnimLockBudget    { get; set; }
    public float? QueueBudget       { get; set; }
    public int?   ComboGraceMs      { get; set; }
    public int?   ChainResetSeconds { get; set; }
}
