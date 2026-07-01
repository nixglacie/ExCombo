using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ExCombo.Helpers;

public static class DrawHelpers {
    public static void DrawIcon(ImDrawListPtr dl, IDalamudTextureWrap tex, Vector2 pos, Vector2 size,
        float opacity = 1f, float rounding = 0f) {
        var col = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, opacity));
        if (rounding > 0f)
            dl.AddImageRounded(tex.Handle, pos, pos + size, Vector2.Zero, Vector2.One, col, rounding);
        else
            dl.AddImage(tex.Handle, pos, pos + size, Vector2.Zero, Vector2.One, col);
    }

    // Dashed rectangle. Used by the live inspector rings so the status border reads distinct from a
    // node's solid border. rounding > 0 gives dashed rounded corners.
    public static void DrawDashedRect(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col,
        float thickness = 2f, float dash = 6f, float gap = 4f, float rounding = 0f) {
        rounding = MathF.Max(0f, MathF.Min(rounding, MathF.Min((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f)));
        void Seg(Vector2 a, Vector2 b, bool overshoot) {
            var dir = b - a;
            var len = dir.Length();
            if (len <= 0f) return;
            dir /= len;
            if (overshoot) {
                // Overshoot both ends by half-thickness so the un-mitered corners fill flush, matching a
                // solid AddRect of the same min/max (otherwise the outer corner reads slightly cut).
                var ht = thickness * 0.5f;
                a  -= dir * ht;
                len += thickness;
            }
            for (var d = 0f; d < len; d += dash + gap) {
                var s = a + dir * d;
                var e = a + dir * MathF.Min(d + dash, len);
                dl.AddLine(s, e, col, thickness);
            }
        }
        void Arc(Vector2 center, float aMin, float aMax) {
            const int Segs = 6;
            var prev = center + new Vector2(MathF.Cos(aMin), MathF.Sin(aMin)) * rounding;
            for (var i = 1; i <= Segs; i++) {
                var t    = aMin + (aMax - aMin) * (i / (float)Segs);
                var next = center + new Vector2(MathF.Cos(t), MathF.Sin(t)) * rounding;
                Seg(prev, next, false);
                prev = next;
            }
        }
        if (rounding <= 0f) {
            var tr = new Vector2(max.X, min.Y);
            var bl = new Vector2(min.X, max.Y);
            Seg(min, tr, true);   // top
            Seg(tr, max, true);   // right
            Seg(max, bl, true);   // bottom
            Seg(bl, min, true);   // left
            return;
        }
        var r = rounding;
        Seg(new Vector2(min.X + r, min.Y), new Vector2(max.X - r, min.Y), false);   // top
        Arc(new Vector2(max.X - r, min.Y + r), -MathF.PI * 0.5f, 0f);
        Seg(new Vector2(max.X, min.Y + r), new Vector2(max.X, max.Y - r), false);   // right
        Arc(new Vector2(max.X - r, max.Y - r), 0f, MathF.PI * 0.5f);
        Seg(new Vector2(max.X - r, max.Y), new Vector2(min.X + r, max.Y), false);   // bottom
        Arc(new Vector2(min.X + r, max.Y - r), MathF.PI * 0.5f, MathF.PI);
        Seg(new Vector2(min.X, max.Y - r), new Vector2(min.X, min.Y + r), false);   // left
        Arc(new Vector2(min.X + r, min.Y + r), MathF.PI, MathF.PI * 1.5f);
    }

    public static void DrawText(ImDrawListPtr dl, Vector2 pos, string text, uint col, bool outline = false) {
        if (outline) {
            uint black = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    dl.AddText(pos + new Vector2(dx, dy), black, text);
        }
        dl.AddText(pos, col, text);
    }
}
