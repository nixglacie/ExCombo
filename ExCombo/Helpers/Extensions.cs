using System;
using System.Numerics;

namespace ExCombo.Helpers;

public static class Extensions {
    public static Vector2 AddX(this Vector2 v, float o) => new(v.X + o, v.Y);
    public static Vector2 AddY(this Vector2 v, float o) => new(v.X, v.Y + o);

    public static bool Evaluate(this CompareOp op, float value, float threshold) => op switch {
        CompareOp.Equals        => MathF.Abs(value - threshold) < 0.01f,
        CompareOp.NotEquals     => MathF.Abs(value - threshold) >= 0.01f,
        CompareOp.LessThan      => value < threshold,
        CompareOp.GreaterThan   => value > threshold,
        CompareOp.LessThanEq    => value <= threshold,
        CompareOp.GreaterThanEq => value >= threshold,
        _                       => false,
    };
}
