using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ExCombo.Helpers;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ExCombo.Windows;

public class OverlayWindow : Window {
    private readonly Configuration _config;

    public OverlayWindow(Configuration config)
        : base("##ExComboOverlay") {
        _config = config;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoNav
              | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDecoration  | ImGuiWindowFlags.NoInputs
              | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings;
        IsOpen             = true;
        RespectCloseHotkey = false;
    }

    public override void PreDraw() {
        Position          = new Vector2(_config.OverlayX, _config.OverlayY);
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw() {
        if (!_config.ShowOverlay) return;
        if (!Plugin.OverlayInCombat) return;

        uint? next = Plugin.OverlayNextAction;   // cached on game thread
        if (next == null) return;

        uint actionId = next.Value;
        bool usable   = Plugin.OverlayNextUsable; // cached on game thread
        uint iconId   = FindIconForAction(actionId);
        float sz      = 64f * _config.OverlayScale;

        var dl   = ImGui.GetWindowDrawList();
        var pos  = ImGui.GetCursorScreenPos();
        var size = new Vector2(sz, sz);
        float radius = sz * 0.15f;

        var tex = Plugin.Textures.GetTextureFromIconId(iconId, greyscale: !usable);
        if (tex != null)
            dl.AddImageRounded(tex.Handle, pos, pos + size,
                Vector2.Zero, Vector2.One, 0xFFFFFFFF, radius, ImDrawFlags.RoundCornersAll);

        uint borderCol = usable
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.455f, 0.765f, 1.000f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.400f, 0.400f, 0.400f, 1f));
        dl.AddRect(pos, pos + size, borderCol, radius, ImDrawFlags.None, 2f);

        ImGui.Dummy(size);
    }

    private static uint FindIconForAction(uint actionId) {
        try {
            var sheet = Plugin.DataManager.GetExcelSheet<LuminaAction>();
            if (sheet == null) return 0;
            return sheet.GetRowOrDefault(actionId)?.Icon ?? 0;
        } catch { return 0; }
    }
}
