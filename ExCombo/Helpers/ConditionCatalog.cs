using System;
using System.Collections.Generic;
using ExCombo.Flow;

namespace ExCombo.Helpers;

// What parameter widget a check needs in the editor.
public enum CheckParamKind { None, Number, ActionId, StatusId, Range }

public readonly record struct CheckCtx(uint ParamId, float Param2, bool TargetIsCurrent, bool SourceAny);

// One sub-check within a condition-family node. Eval returns a float; bool checks return 0/1
// and are compared "== 1". The node's CompareOp/CompareVal is applied to the result.
// Bool=true ⇒ the check yields a true/false value (compared "is true / is false") and the editor
// hides the numeric op/value widgets. HasTarget=true ⇒ offers a Player/Current-target scope toggle.
public sealed record CheckDef(string Key, string Label, CheckParamKind Param, Func<CheckCtx, float> Eval,
    bool Bool = false, bool HasTarget = false, bool RequiresTarget = false, bool HasSource = false);

// Per node-type catalogs of parameterized checks (mirrors JobGaugeRegistry).
internal static class ConditionCatalog {
    private static float B(bool v) => v ? 1f : 0f;

    private static readonly List<CheckDef> _status = new() {
        // HasSource toggles the source scope: "me" (applied by the player) vs "anyone" (any owner,
        // e.g. reading a raid buff someone else cast on you).
        new("Present",   "Status present",        CheckParamKind.StatusId, c => B(StatusHelper.Present(c.ParamId, c.TargetIsCurrent, sourcePlayer: !c.SourceAny)),   Bool: true, HasTarget: true, HasSource: true),
        new("Remaining", "Status remaining (s)",  CheckParamKind.StatusId, c => StatusHelper.Remaining(c.ParamId, c.TargetIsCurrent, sourcePlayer: !c.SourceAny),    HasTarget: true, HasSource: true),
        new("Stacks",    "Status stacks",         CheckParamKind.StatusId, c => StatusHelper.Stacks(c.ParamId, c.TargetIsCurrent, sourcePlayer: !c.SourceAny),       HasTarget: true, HasSource: true),
    };

    private static readonly List<CheckDef> _cooldown = new() {
        new("Ready",     "Action ready",           CheckParamKind.ActionId, c => B(CooldownHelper.Ready(c.ParamId)), Bool: true),
        new("Remaining", "Cooldown remaining (s)", CheckParamKind.ActionId, c => CooldownHelper.Remaining(c.ParamId)),
        new("Elapsed",     "Cooldown elapsed (s)",  CheckParamKind.ActionId, c => CooldownHelper.Elapsed(c.ParamId)),
        new("Charges",   "Charges",                CheckParamKind.ActionId, c => CooldownHelper.Charges(c.ParamId)),
        new("LevelChecked","Level met for action",  CheckParamKind.ActionId, c => B(CooldownHelper.LevelChecked(c.ParamId)), Bool: true),
    };

    private static readonly List<CheckDef> _target = new() {
        new("TargetHpPercent", "Target HP %",          CheckParamKind.None,  _ => TargetHelper.TargetHpPercent(),           RequiresTarget: true),
        new("TargetHpShield",  "Target HP % (+shield)",CheckParamKind.None,  _ => TargetHelper.TargetHpPercentWithShield(),              RequiresTarget: true),
        new("TargetDistance",  "Target distance (y)",  CheckParamKind.None,  _ => TargetHelper.TargetDistance(),            RequiresTarget: true),
        new("InRange",         "Target in range (y)",  CheckParamKind.Range, c => B(TargetHelper.InRange(c.Param2)),         Bool: true,  RequiresTarget: true),
        new("InMeleeRange",    "In melee range",       CheckParamKind.None,  _ => B(TargetHelper.InMeleeRange()),           Bool: true,  RequiresTarget: true),
        new("TargetIsCasting", "Target casting (≥%)",  CheckParamKind.Range, c => B(TargetHelper.TargetIsCasting(c.Param2)),Bool: true,  RequiresTarget: true),
        new("Interruptible",   "Target interruptible (≥%)", CheckParamKind.Range, c => B(TargetHelper.TargetInterruptible(c.Param2)),   Bool: true, RequiresTarget: true),
        new("OnFront",         "On target's front",    CheckParamKind.None,  _ => B(TargetHelper.OnFront()),                Bool: true,  RequiresTarget: true),
        new("OnFlank",         "On target's flank",    CheckParamKind.None,  _ => B(TargetHelper.OnFlank()),                Bool: true,  RequiresTarget: true),
        new("OnRear",          "On target's rear",     CheckParamKind.None,  _ => B(TargetHelper.OnRear()),                 Bool: true,  RequiresTarget: true),
        new("NeedsPositionals","Target needs positionals", CheckParamKind.None, _ => B(TargetHelper.TargetNeedsPositionals()), Bool: true, RequiresTarget: true),
        new("IsBoss",          "Target is boss",       CheckParamKind.None,  _ => B(TargetHelper.TargetIsBoss()),           Bool: true,  RequiresTarget: true),
        new("EnemiesInRange",  "Enemies in range (y)", CheckParamKind.Range, c => TargetHelper.EnemiesInRange(c.Param2),    RequiresTarget: true),
        new("EnemiesInAoe",    "Enemies in action AoE",CheckParamKind.ActionId, c => TargetHelper.EnemiesInAoe(c.ParamId)),
    };

    private static readonly List<CheckDef> _player = new() {
        new("PlayerHpPercent", "Player HP %",          CheckParamKind.None, _ => TargetHelper.PlayerHpPercent()),
        new("PlayerMpPercent", "Player MP %",          CheckParamKind.None, _ => TargetHelper.PlayerMpPercent()),
        new("PlayerMp",        "Player MP",            CheckParamKind.None, _ => TargetHelper.PlayerMp()),
        new("InCombat",        "In combat",            CheckParamKind.None, _ => B(PlayerStateHelper.InCombat()),   Bool: true),
        new("CombatTime",      "Combat time (s)",      CheckParamKind.None, _ => PlayerStateHelper.CombatTime()),
        new("IsMoving",        "Is moving",            CheckParamKind.None, _ => B(PlayerStateHelper.IsMoving()),   Bool: true),
        new("TimeMoving",      "Time moving (s)",      CheckParamKind.None, _ => PlayerStateHelper.TimeMoving()),
        new("TimeStoodStill",  "Time stood still (s)", CheckParamKind.None, _ => PlayerStateHelper.TimeStoodStill()),
        new("IsOccupied",      "Is occupied",          CheckParamKind.None, _ => B(PlayerStateHelper.IsOccupied()), Bool: true),
        new("CountdownActive", "Countdown active",     CheckParamKind.None, _ => B(PlayerStateHelper.CountdownActive()), Bool: true),
        new("CountdownRemaining","Countdown remaining (s)", CheckParamKind.None, _ => PlayerStateHelper.CountdownRemaining()),
        new("LimitBreakLevel", "Limit break level",    CheckParamKind.None, _ => PlayerStateHelper.LimitBreakLevel()),
        new("HasTankStance",   "Tank stance on",       CheckParamKind.None, _ => B(PlayerStateHelper.HasTankStance()),  Bool: true),
        new("HasPet",          "Pet present",          CheckParamKind.None, _ => B(PlayerStateHelper.HasPetPresent()),  Bool: true),
        new("PlayerHasAggro",  "Player has aggro",     CheckParamKind.None, _ => B(EnmityHelper.PlayerHasAggro()),      Bool: true),
        new("InDuty",          "In duty",              CheckParamKind.None, _ => B(PlayerStateHelper.InDuty()),         Bool: true),
        new("InFate",          "In FATE",              CheckParamKind.None, _ => B(PlayerStateHelper.InFATE()),         Bool: true),
    };

    private static readonly List<CheckDef> _party = new() {
        new("AvgHpPercent",    "Party avg HP %",        CheckParamKind.None,     _ => PartyHelper.AvgHpPercent()),
        new("MembersWithBuff", "Members with buff",     CheckParamKind.StatusId, c => PartyHelper.MembersWithBuff(c.ParamId)),
        new("DeadMembers",     "Dead members",          CheckParamKind.None,     _ => PartyHelper.DeadCount()),
        new("PartyInCombat",   "Party in combat",       CheckParamKind.None,     _ => B(PartyHelper.AnyInCombat()),     Bool: true),
    };

    // Gate on the player's own recent casts (ActionTracker). "Within N sec" = TimeSinceUsed < N.
    private static readonly List<CheckDef> _actionHistory = new() {
        new("WasLast",        "Was last action",       CheckParamKind.ActionId, c => B(ActionTracker.WasLast(c.ParamId)),      Bool: true),
        new("TimeSinceUsed",  "Time since used (s)",   CheckParamKind.ActionId, c => ActionTracker.TimeSinceUsed(c.ParamId)),
        new("UseCount",       "Uses this combat",      CheckParamKind.ActionId, c => ActionTracker.Count(c.ParamId)),
        new("LastWeaponskill","Last was weaponskill",  CheckParamKind.None,     _ => B(ActionTracker.LastWasCategory(3)),      Bool: true),
        new("LastSpell",      "Last was spell",        CheckParamKind.None,     _ => B(ActionTracker.LastWasCategory(2)),      Bool: true),
        new("LastAbility",    "Last was ability",      CheckParamKind.None,     _ => B(ActionTracker.LastWasCategory(4)),      Bool: true),
    };

    public static IReadOnlyList<CheckDef>? For(NodeType t) => t switch {
        NodeType.StatusCondition   => _status,
        NodeType.CooldownCondition => _cooldown,
        NodeType.TargetCondition   => _target,
        NodeType.PlayerCondition   => _player,
        NodeType.PartyCondition    => _party,
        NodeType.ActionHistoryCondition => _actionHistory,
        _ => null,
    };

    public static CheckDef? Find(NodeType t, string key) {
        var list = For(t);
        if (list == null) return null;
        foreach (var d in list) if (d.Key == key) return d;
        return null;
    }

    // ── Job-gauge family (job-scoped, built from JobGaugeRegistry) ───────────────────────────────
    // The gauge field list depends on the flow's job, so these can't live in the static per-NodeType
    // catalogs. Fields whose name reads boolean (Has*/Is*/In*, which the registry stores as 0/1) get
    // the is-true/false widget; everything else stays a numeric compare.
    private static readonly Dictionary<string, IReadOnlyList<CheckDef>> _gaugeCache = new();

    private static bool IsBoolName(string name)
        => name.StartsWith("Has", StringComparison.Ordinal)
        || name.StartsWith("Is",  StringComparison.Ordinal)
        || name.StartsWith("In",  StringComparison.Ordinal);

    public static IReadOnlyList<CheckDef> ForGauge(string job) {
        if (_gaugeCache.TryGetValue(job, out var cached)) return cached;
        var fields = JobGaugeRegistry.GetFields(job);
        var list = new List<CheckDef>();
        if (fields != null)
            foreach (var f in fields) {
                var get = f.Get;   // capture per field
                list.Add(new CheckDef(f.Name, f.Name, CheckParamKind.None, _ => get(), Bool: IsBoolName(f.Name)));
            }
        _gaugeCache[job] = list;
        return list;
    }

    public static CheckDef? FindGauge(string job, string key) {
        foreach (var d in ForGauge(job)) if (d.Key == key) return d;
        return null;
    }
}
