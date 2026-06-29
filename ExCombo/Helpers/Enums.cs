using System;

namespace ExCombo.Helpers;

public enum CompareOp { Eq = 0, Neq = 1, Lt = 2, Lte = 3, Gt = 4, Gte = 5 }

internal static class CompareOpExtensions {
    public static bool Evaluate(this CompareOp op, float a, float b) => op switch {
        CompareOp.Eq  => MathF.Abs(a - b) < 0.001f,
        CompareOp.Neq => MathF.Abs(a - b) >= 0.001f,
        CompareOp.Lt  => a < b,
        CompareOp.Lte => a <= b,
        CompareOp.Gt  => a > b,
        CompareOp.Gte => a >= b,
        _ => false
    };

    public static string ToLabel(this CompareOp op) => op switch {
        CompareOp.Eq  => "==",
        CompareOp.Neq => "!=",
        CompareOp.Lt  => "<",
        CompareOp.Lte => "≤",
        CompareOp.Gt  => ">",
        CompareOp.Gte => "≥",
        _ => "?"
    };
}
