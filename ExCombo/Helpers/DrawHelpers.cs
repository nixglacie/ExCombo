using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ExCombo.Helpers;

public static class DrawHelpers {
    public static void DrawIcon(ImDrawListPtr dl, IDalamudTextureWrap tex, Vector2 pos, Vector2 size, float opacity = 1f) {
        dl.AddImage(tex.Handle, pos, pos + size, Vector2.Zero, Vector2.One,
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, opacity)));
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
