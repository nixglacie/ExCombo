using System.Numerics;

namespace ExCombo.Helpers;

public static class Extensions {
    public static Vector2 AddX(this Vector2 v, float o) => new(v.X + o, v.Y);
    public static Vector2 AddY(this Vector2 v, float o) => new(v.X, v.Y + o);
}
