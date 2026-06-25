using System;
using ExCombo.Helpers;

namespace ExCombo.Flow;

public enum NodeType { Trigger, Action, Group, Condition }

public enum ConditionKind {
    LastAction        = 0,
    HasStatus         = 1,
    CooldownReady     = 2,
    InCombat          = 3,
    WeaponDrawn       = 4,
    ActionUsable      = 5,
    ActionHighlighted = 6,
    ActionInRange     = 7,
    TargetInLoS       = 8,
    JobResource       = 9,
    CanWeave          = 10,
}

public class FlowNode {
    public string   Id          { get; set; } = Guid.NewGuid().ToString();
    public NodeType Type        { get; set; }
    public float    X           { get; set; } = 100f;
    public float    Y           { get; set; } = 100f;

    public uint   ActionId    { get; set; }
    public string ActionLabel { get; set; } = "";
    public uint   IconId      { get; set; }

    public int Priority { get; set; } = 1;

    // Condition node fields
    public ConditionKind ConditionKind        { get; set; }
    public StatusSource  ConditionSource      { get; set; }
    public uint          ConditionParam       { get; set; }
    public string        ConditionParamLabel  { get; set; } = "";
    public uint          ConditionParamIconId { get; set; }
    public int           CompareOp            { get; set; } = 4; // LessThanEq
    public float         CompareVal           { get; set; }
}
