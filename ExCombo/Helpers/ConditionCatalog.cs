using System;
using System.Collections.Generic;
using ExCombo.Flow;

namespace ExCombo.Helpers;

// What parameter widget a check needs in the editor.
public enum CheckParamKind { None, Number, ActionId, StatusId, Range }

public readonly record struct CheckCtx(uint ParamId, float Param2, bool TargetIsCurrent);

// One sub-check within a condition-family node. Eval returns a float; bool checks return 0/1
// and are compared "== 1". The node's CompareOp/CompareVal is applied to the result.
// Bool=true ⇒ the check yields a true/false value (compared "is true / is false") and the editor
// hides the numeric op/value widgets. HasTarget=true ⇒ offers a Player/Current-target scope toggle.
public sealed record CheckDef(string Key, string Label, CheckParamKind Param, Func<CheckCtx, float> Eval,
    bool Bool = false, bool HasTarget = false, bool RequiresTarget = false);

// Per node-type catalogs of parameterized checks (mirrors JobGaugeRegistry).
internal static class ConditionCatalog {
    private static float B(bool v) => v ? 1f : 0f;

    private static readonly List<CheckDef> _status = new() {
        new("Present",   "Status present",        CheckParamKind.StatusId, c => B(StatusHelper.Present(c.ParamId, c.TargetIsCurrent)),   Bool: true, HasTarget: true),
        new("Remaining", "Status remaining (s)",  CheckParamKind.StatusId, c => StatusHelper.Remaining(c.ParamId, c.TargetIsCurrent),    HasTarget: true),
        new("Stacks",    "Status stacks",         CheckParamKind.StatusId, c => StatusHelper.Stacks(c.ParamId, c.TargetIsCurrent),       HasTarget: true),
    };

    private static readonly List<CheckDef> _cooldown = new() {
        new("Remaining", "Cooldown remaining (s)", CheckParamKind.ActionId, c => CooldownHelper.Remaining(c.ParamId)),
        new("Charges",   "Charges",                CheckParamKind.ActionId, c => CooldownHelper.Charges(c.ParamId)),
        new("Ready",     "Action ready",           CheckParamKind.ActionId, c => B(CooldownHelper.Ready(c.ParamId)), Bool: true),
    };

    private static readonly List<CheckDef> _target = new() {
        new("PlayerHpPercent", "Player HP %",          CheckParamKind.None,  _ => TargetHelper.PlayerHpPercent()),
        new("TargetHpPercent", "Target HP %",          CheckParamKind.None,  _ => TargetHelper.TargetHpPercent(),           RequiresTarget: true),
        new("TargetDistance",  "Target distance (y)",  CheckParamKind.None,  _ => TargetHelper.TargetDistance(),            RequiresTarget: true),
        new("InRange",         "Target in range (y)",  CheckParamKind.Range, c => B(TargetHelper.InRange(c.Param2)),         Bool: true,  RequiresTarget: true),
        new("InMeleeRange",    "In melee range",       CheckParamKind.None,  _ => B(TargetHelper.InMeleeRange()),           Bool: true,  RequiresTarget: true),
        new("TargetIsCasting", "Target casting (≥%)",  CheckParamKind.Range, c => B(TargetHelper.TargetIsCasting(c.Param2)),Bool: true,  RequiresTarget: true),
        new("EnemiesInRange",  "Enemies in range (y)", CheckParamKind.Range, c => TargetHelper.EnemiesInRange(c.Param2),    RequiresTarget: true),
        new("OnFront",         "On target's front",    CheckParamKind.None,  _ => B(TargetHelper.OnFront()),                Bool: true,  RequiresTarget: true),
        new("OnFlank",         "On target's flank",    CheckParamKind.None,  _ => B(TargetHelper.OnFlank()),                Bool: true,  RequiresTarget: true),
        new("OnRear",          "On target's rear",     CheckParamKind.None,  _ => B(TargetHelper.OnRear()),                 Bool: true,  RequiresTarget: true),
    };

    private static readonly List<CheckDef> _player = new() {
        new("InCombat",        "In combat",            CheckParamKind.None, _ => B(PlayerStateHelper.InCombat()),   Bool: true),
        new("IsMoving",        "Is moving",            CheckParamKind.None, _ => B(PlayerStateHelper.IsMoving()),   Bool: true),
        new("IsOccupied",      "Is occupied",          CheckParamKind.None, _ => B(PlayerStateHelper.IsOccupied()), Bool: true),
        new("CombatTime",      "Combat time (s)",      CheckParamKind.None, _ => PlayerStateHelper.CombatTime()),
        new("LimitBreakLevel", "Limit break level",    CheckParamKind.None, _ => PlayerStateHelper.LimitBreakLevel()),
    };

    private static readonly List<CheckDef> _party = new() {
        new("AvgHpPercent",    "Party avg HP %",        CheckParamKind.None,     _ => PartyHelper.AvgHpPercent()),
        new("MembersWithBuff", "Members with buff",     CheckParamKind.StatusId, c => PartyHelper.MembersWithBuff(c.ParamId)),
        new("DeadMembers",     "Dead members",          CheckParamKind.None,     _ => PartyHelper.DeadCount()),
        new("PlayerHasAggro",  "Player has aggro",      CheckParamKind.None,     _ => B(EnmityHelper.PlayerHasAggro()), Bool: true),
    };

    public static IReadOnlyList<CheckDef>? For(NodeType t) => t switch {
        NodeType.StatusCondition   => _status,
        NodeType.CooldownCondition => _cooldown,
        NodeType.TargetCondition   => _target,
        NodeType.PlayerCondition   => _player,
        NodeType.PartyCondition    => _party,
        _ => null,
    };

    public static CheckDef? Find(NodeType t, string key) {
        var list = For(t);
        if (list == null) return null;
        foreach (var d in list) if (d.Key == key) return d;
        return null;
    }
}
