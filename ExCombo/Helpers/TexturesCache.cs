using System;
using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Utility;
using Lumina.Data.Files;

namespace ExCombo.Helpers;

public class TexturesCache : IDisposable {
    private readonly Dictionary<uint, IDalamudTextureWrap> _grey = [];

    public IDalamudTextureWrap? GetTextureFromIconId(uint iconId, uint stackCount = 0, bool hd = true, bool greyscale = false) {
        if (!greyscale) {
            try { return Plugin.TextureProvider.GetFromGameIcon(iconId + stackCount).GetWrapOrDefault(); }
            catch { return null; }
        }
        uint key = iconId + stackCount;
        if (_grey.TryGetValue(key, out var cached)) return cached;
        try {
            string? path = Plugin.TextureProvider.GetIconPath(new GameIconLookup(iconId: iconId, hiRes: hd));
            if (path == null) return null;
            var file = Plugin.DataManager.GetFile<TexFile>(path);
            if (file == null) return null;
            byte[] bytes = file.GetRgbaImageData();
            Desaturate(ref bytes);
            var tex = Plugin.TextureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(file.Header.Width, file.Header.Height), bytes);
            if (tex != null) _grey[key] = tex;
            return tex;
        } catch { return null; }
    }

    private static void Desaturate(ref byte[] b) {
        if (b.Length % 4 != 0) return;
        for (int i = 0; i < b.Length; i += 4) {
            byte lum = (byte)((b[i] >> 2) + (b[i + 1] >> 1) + (b[i + 2] >> 3));
            b[i] = b[i + 1] = b[i + 2] = lum;
        }
    }

    public void Dispose() {
        foreach (var t in _grey.Values) t.Dispose();
        _grey.Clear();
    }
}
