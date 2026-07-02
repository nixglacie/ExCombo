using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using LuminaAction = Lumina.Excel.Sheets.Action;

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

    public static float PlayerMp() => Plugin.ObjectTable.LocalPlayer?.CurrentMp ?? 0f;

    public static float PlayerMpPercent() {
        var p = Plugin.ObjectTable.LocalPlayer;
        return p is { MaxMp: > 0 } ? p.CurrentMp * 100f / p.MaxMp : 0f;
    }

    public static float TargetHpPercent() {
        var t = Target;
        return t is { MaxHp: > 0 } ? t.CurrentHp * 100f / t.MaxHp : 0f;
    }

    // Target HP% including its damage shield (from Wrath GetTargetHPPercent(includeShield)).
    public static float TargetHpPercentWithShield() {
        var t = Target;
        if (t is not { MaxHp: > 0 }) return 0f;
        var effective = t.CurrentHp + (ulong)(t.MaxHp * (t.ShieldPercentage / 100f));
        return effective * 100f / t.MaxHp;
    }

    // Target is casting an interruptible spell past the given cast-completion threshold (from Wrath
    // Target.CanInterruptEnemy). Distinct from TargetIsCasting, which ignores interruptibility.
    public static bool TargetInterruptible(float minCastPercent = 0f) {
        var t = Target;
        if (t is not { IsCasting: true } || !t.IsCastInterruptible) return false;
        if (t.TotalCastTime <= 0f) return minCastPercent <= 0f;
        return t.CurrentCastTime / t.TotalCastTime * 100f >= minCastPercent;
    }

    // Target is a boss (from Wrath Target.TargetIsBoss). Trimmed to the NameId==541 nameplate marker;
    // Wrath's extra hardcoded BaseId list isn't available in this port.
    public static bool TargetIsBoss()
        => Plugin.TargetManager.Target is IBattleNpc { NameId: 541 };

    // Target still cares about positionals (from Wrath Target.TargetNeedsPositionals). Trimmed to the
    // 3808 "no positionals" immunity buff (striking dummies); the omnidirectional-NPC table isn't
    // available in this port.
    public static bool TargetNeedsPositionals()
        => Target != null && !StatusHelper.Present(3808, targetIsCurrent: true, sourcePlayer: false);

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

    // ── AoE-shape enemy count (from Wrath Target.EnemiesInRange / ObjectsInRange) ─────────────
    // Counts hostile NPCs actually caught by the given action's real AoE shape (circle / cone / line)
    // rather than a naive circle. LoS filtering from the Wrath original is omitted in this port.
    public static int EnemiesInAoe(uint actionId) {
        var row = Plugin.DataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        if (row is null) return 0;
        var a = row.Value;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return 0;

        float effect = a.EffectRange;
        float range  = a.Range;
        var castType = a.CastType;
        var target   = Plugin.TargetManager.Target;

        // Circle-on-target, cone and line all need an anchor target; self-circle does not.
        bool selfCircle = castType == 2 && a.CanTargetSelf;
        if (!selfCircle && castType != 1 && target == null) return 0;

        var pPos = player.Position;
        var count = 0;
        foreach (var o in Plugin.ObjectTable) {
            if (o is not IBattleNpc npc || (byte)npc.BattleNpcKind != 5 /* Enemy */) continue;
            if (npc.CurrentHp == 0) continue;
            var hit = castType switch {
                1 => TargetDistance(npc) <= range,                                        // single-target range
                2 when selfCircle => PointInCircle(npc.Position - pPos, effect + npc.HitboxRadius),
                2 => PointInCircle(npc.Position - target!.Position, effect + npc.HitboxRadius),
                3 => TargetDistance(npc) <= range
                     && PointInCone(npc.Position - pPos, Direction(pPos, target!.Position), 45f),
                4 => TargetDistance(npc) <= range
                     && HitboxInRect(npc, pPos, Rotation(pPos, target!.Position), range * 0.5f, a.XAxisModifier * 0.5f),
                _ => false,
            };
            if (hit) count++;
        }
        return count;
    }

    private static float Rotation(Vector3 a, Vector3 b) => MathF.Atan2(b.X - a.X, b.Z - a.Z);
    private static Vector3 Direction(Vector3 a, Vector3 b) {
        var r = Rotation(a, b);
        return new Vector3(MathF.Sin(r), 0f, MathF.Cos(r));
    }
    private static bool PointInCircle(Vector3 offset, float radius) => offset.LengthSquared() <= radius * radius;
    private static bool PointInCone(Vector3 offset, Vector3 dir, float halfAngleDeg)
        => Vector3.Dot(Vector3.Normalize(offset), dir) > MathF.Cos(halfAngleDeg * (MathF.PI / 180f));

    private static bool HitboxInRect(IGameObject o, Vector3 playerPos, float rotation, float halfLength, float halfWidth) {
        var A = new Vector2(playerPos.X, playerPos.Z);
        var d = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
        var n = new Vector2(d.Y, -d.X);
        var P = new Vector2(o.Position.X, o.Position.Z);
        var R = o.HitboxRadius;
        var Q = A + d * halfLength;
        var P2 = P - Q;
        var Ptrans  = new Vector2(Vector2.Dot(P2, n), Vector2.Dot(P2, d));
        var Pcorner = new Vector2(MathF.Abs(Ptrans.X) - halfWidth, MathF.Abs(Ptrans.Y) - halfLength);
        if (Pcorner.X > R || Pcorner.Y > R) return false;
        if (Pcorner.X <= 0 || Pcorner.Y <= 0) return true;
        return Pcorner.LengthSquared() <= R * R;
    }
}
