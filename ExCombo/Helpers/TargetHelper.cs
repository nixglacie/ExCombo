using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace ExCombo.Helpers;

// Target state / range / positionals (ported & trimmed from Wrath Functions/Target.cs).
internal static class TargetHelper {
    private static IBattleChara? Target => Plugin.TargetManager.Target as IBattleChara;

    // A valid battle target exists (soft/friendly/object targets don't count).
    public static bool HasTarget() => Target != null;

    public static float PlayerHpPercent() {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p is { MaxHp: > 0 } ? p.CurrentHp * 100f / p.MaxHp : 0f;
    }

    public static float TargetHpPercent() {
        var t = Target;
        return t is { MaxHp: > 0 } ? t.CurrentHp * 100f / t.MaxHp : 0f;
    }

    // Horizontal distance between two characters minus both hitbox radii (yalms to "edge").
    public static float TargetDistance(IGameObject? optionalTarget = null) {
        var src = Plugin.ObjectTable.LocalPlayer;
        var tgt = optionalTarget ?? Plugin.TargetManager.Target;
        if (src == null || tgt == null) return float.MaxValue;
        var a = new Vector2(tgt.Position.X, tgt.Position.Z);
        var b = new Vector2(src.Position.X, src.Position.Z);
        return MathF.Max(0f, Vector2.Distance(a, b) - tgt.HitboxRadius - src.HitboxRadius);
    }

    public static bool InRange(float range) => TargetDistance() <= range;
    public static bool InMeleeRange() => Target != null && TargetDistance() <= 3f;

    public static bool TargetIsCasting(float minCastPercent = 0f) {
        var t = Target;
        if (t is not { IsCasting: true }) return false;
        if (t.TotalCastTime <= 0f) return minCastPercent <= 0f;
        return t.CurrentCastTime / t.TotalCastTime * 100f >= minCastPercent;
    }

    // Count hostile battle NPCs within `range` yalms of the current target (circle approximation).
    public static int EnemiesInRange(float range) {
        var anchor = Plugin.TargetManager.Target;
        if (anchor == null) return 0;
        var ap = new Vector2(anchor.Position.X, anchor.Position.Z);
        var count = 0;
        foreach (var o in Plugin.ObjectTable) {
            if (o is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5 /* Enemy */) continue;
            if (npc.CurrentHp == 0) continue;
            var p = new Vector2(npc.Position.X, npc.Position.Z);
            if (Vector2.Distance(ap, p) - npc.HitboxRadius <= range) count++;
        }
        return count;
    }

    // ── Positionals (from Wrath PositionalMath) ──────────────────────────────
    private enum AttackAngle { Front, Flank, Rear, Unknown }

    private static AttackAngle AngleToTarget() {
        var player = Plugin.ObjectTable.LocalPlayer;
        var target = Target;
        if (player == null || target == null) return AttackAngle.Unknown;
        float rotation = MathF.Atan2(player.Position.X - target.Position.X,
                                     player.Position.Z - target.Position.Z) - target.Rotation;
        float deg = rotation * (180f / MathF.PI) + (rotation < 0f ? 360f : 0f);
        return deg switch {
            >= 315f or <= 45f   => AttackAngle.Front,
            >= 45f and <= 135f  => AttackAngle.Flank,
            >= 135f and <= 225f => AttackAngle.Rear,
            >= 225f and <= 315f => AttackAngle.Flank,
            _ => AttackAngle.Unknown,
        };
    }

    public static bool OnFront() => AngleToTarget() == AttackAngle.Front;
    public static bool OnFlank() => AngleToTarget() == AttackAngle.Flank;
    public static bool OnRear()  => AngleToTarget() == AttackAngle.Rear;
}
