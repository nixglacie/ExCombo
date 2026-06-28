using System;

namespace ExCombo.Flow;

public enum NodeType { }

public class FlowNode {
    public string   Id   { get; set; } = Guid.NewGuid().ToString();
    public NodeType Type { get; set; }
    public float    X    { get; set; } = 100f;
    public float    Y    { get; set; } = 100f;
}
