using System;

namespace ExCombo.Flow;

public enum NodeType {
    Trigger   = 0,
    Action    = 1,
    Branch    = 2,
    Condition = 3,
}

public class FlowNode {
    public string   Id          { get; set; } = Guid.NewGuid().ToString();
    public NodeType Type        { get; set; }
    public float    X           { get; set; } = 100f;
    public float    Y           { get; set; } = 100f;
    public uint     ActionId    { get; set; }
    public string   ActionLabel { get; set; } = "";
    public uint     IconId      { get; set; }
    public int      OutputCount { get; set; } = 2;

    // Condition node fields
    public string ConditionField      { get; set; } = "";
    public int    ConditionCompareOp  { get; set; } = 5;  // CompareOp.Gte
    public float  ConditionCompareVal { get; set; } = 1f;

    // True when CooldownGroup != 57 (not on the GCD recast track)
    public bool IsOgcd { get; set; } = false;
}
