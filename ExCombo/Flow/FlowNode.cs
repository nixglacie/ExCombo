using System;

namespace ExCombo.Flow;

public enum NodeType {
    Trigger          = 0,
    Action           = 1,
    Branch           = 2,
    Condition        = 3,   // job-gauge gate (legacy)
    Note             = 4,
    StatusCondition  = 5,
    CooldownCondition= 6,
    TargetCondition  = 7,
    PlayerCondition  = 8,
    PartyCondition   = 9,
}

// Built-in target resolvers for Action-node retargeting (Phase 3).
public enum RetargetMode {
    None           = 0,
    Self           = 1,
    LowestHpAlly   = 2,
    TargetOfTarget = 3,
    LowestHpEnemy  = 4,
    DeadMember     = 5,
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

    // Generic storage shared by the parameterized condition-family nodes (Status/Cooldown/
    // Target/Player/Party). Reuses ConditionCompareOp/ConditionCompareVal for the numeric compare.
    public string CheckField   { get; set; } = "";   // sub-check key within the node's category
    public uint   CheckParamId { get; set; } = 0;     // action/status id parameter (picker)
    public float  CheckParam2  { get; set; } = 0f;    // secondary numeric (range, etc.)
    public int    CheckTarget  { get; set; } = 0;     // 0=Self/Player, 1=CurrentTarget

    // Built-in retarget resolver for Action nodes (Phase 3); 0 = None.
    public int RetargetMode { get; set; } = 0;

    // Cached oGCD hint for the editor UI; the executor derives this live via ActionHelper.IsOgcd.
    public bool IsOgcd { get; set; } = false;

    // Free text for Note nodes (no input/output; pure comment, ignored by the executor).
    public string NoteText { get; set; } = "";
    // Note node box size (grid-snapped, user-resizable).
    public float  NoteW    { get; set; } = 160f;
    public float  NoteH    { get; set; } = 64f;

    // Non-null id shared by nodes forming an atomic combo group (runs to completion before
    // the branch re-evaluates priority). Null = ungrouped.
    public string? GroupId { get; set; } = null;

    // Field-by-field copy (keeps Id; callers reassign Id when duplicating).
    public FlowNode Clone() => new() {
        Id = Id, Type = Type, X = X, Y = Y, ActionId = ActionId, ActionLabel = ActionLabel,
        IconId = IconId, OutputCount = OutputCount,
        ConditionField = ConditionField, ConditionCompareOp = ConditionCompareOp, ConditionCompareVal = ConditionCompareVal,
        CheckField = CheckField, CheckParamId = CheckParamId, CheckParam2 = CheckParam2, CheckTarget = CheckTarget,
        RetargetMode = RetargetMode, IsOgcd = IsOgcd,
        NoteText = NoteText, NoteW = NoteW, NoteH = NoteH, GroupId = GroupId,
    };

    // All condition-family node types behave as 2-port gates (port0=true, port1=false).
    public static bool IsGate(NodeType t) => t is NodeType.Condition
        or NodeType.StatusCondition or NodeType.CooldownCondition
        or NodeType.TargetCondition or NodeType.PlayerCondition
        or NodeType.PartyCondition;

    public bool IsGate() => IsGate(Type);
}
