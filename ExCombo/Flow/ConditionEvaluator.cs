using System;
using ExCombo.Helpers;
using ExCombo.Helpers.DataSources;

namespace ExCombo.Flow;

public static class ConditionEvaluator {
    public static bool Eval(FlowNode node) {
        switch (node.ConditionKind) {
            case ConditionKind.LastAction: {
                uint last     = Plugin.Actions.GetLastUsedActionId();
                uint param    = node.ConditionParam;
                uint adjParam = Plugin.Actions.GetAdjustedActionId(param);
                return last == param || last == adjParam;
            }
            case ConditionKind.HasStatus:
                return Plugin.Statuses.GetStatusList(node.ConditionSource, node.ConditionParam).Count > 0;
            case ConditionKind.CooldownReady: {
                Plugin.Actions.GetAdjustedRecastInfo(node.ConditionParam, out var ri);
                float remaining = MathF.Max(0f, ri.RecastTime - ri.RecastTimeElapsed);
                return Compare(remaining, (CompareOp)node.CompareOp, node.CompareVal);
            }
            case ConditionKind.InCombat:          return CharacterState.IsInCombat();
            case ConditionKind.WeaponDrawn:       return CharacterState.IsWeaponDrawn();
            case ConditionKind.ActionUsable:      return Plugin.Actions.CanUseAction(node.ConditionParam);
            case ConditionKind.ActionHighlighted: return Plugin.Actions.IsActionHighlighted(Plugin.Actions.GetAdjustedActionId(node.ConditionParam));
            case ConditionKind.ActionInRange:     return Plugin.Actions.ActionInRange(Plugin.Actions.GetAdjustedActionId(node.ConditionParam));
            case ConditionKind.TargetInLoS:       return Plugin.Actions.TargetInLoS(Plugin.Actions.GetAdjustedActionId(node.ConditionParam));
            case ConditionKind.CanWeave: {
                ActionHelpers.GetGCDInfo(out var gcdRi);
                if (gcdRi.RecastTime <= 0f) return false;
                float remaining = MathF.Max(0f, gcdRi.RecastTime - gcdRi.RecastTimeElapsed);
                if (remaining >= gcdRi.RecastTime) return false;
                if (Plugin.Actions.GetAnimationLock() > 0f) return false;
                return node.ConditionParam switch {
                    1 => remaining > gcdRi.RecastTime * 0.45f,           // Early
                    2 => remaining <= gcdRi.RecastTime * 0.45f && remaining > 0f, // Late
                    _ => true,                                            // Any
                };
            }
            case ConditionKind.JobResource: {
                var job = CharacterState.GetCharacterJob();
                var ds  = JobDataSourceRegistry.GetForJob(job);
                if (ds == null) return false;
                ds.Update();
                return ((CompareOp)node.CompareOp).Evaluate(ds.GetConditionValue((int)node.ConditionParam), node.CompareVal);
            }
            default: return false;
        }
    }

    private static bool Compare(float value, CompareOp op, float threshold) => op switch {
        CompareOp.Equals        => MathF.Abs(value - threshold) < 0.01f,
        CompareOp.NotEquals     => MathF.Abs(value - threshold) >= 0.01f,
        CompareOp.LessThan      => value < threshold,
        CompareOp.GreaterThan   => value > threshold,
        CompareOp.LessThanEq    => value <= threshold,
        CompareOp.GreaterThanEq => value >= threshold,
        _ => false,
    };
}
