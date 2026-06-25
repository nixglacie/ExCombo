using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ExCombo.Windows;

public class ConfigWindow : Window {
    private readonly Configuration _config;

    public ConfigWindow(Configuration config) : base("ExCombo Settings###ExComboConfig") {
        _config = config;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(300, 160),
            MaximumSize = new Vector2(600, 400),
        };
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        ImGui.TextWrapped("ExCombo v1.0 — Node-based combat rotation editor.");
        ImGui.Spacing();
        ImGui.TextDisabled("Use /excombo to open the flow editor.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Overlay");
        ImGui.Spacing();

        bool show = _config.ShowOverlay;
        if (ImGui.Checkbox("Show Overlay", ref show)) {
            _config.ShowOverlay = show;
            _config.Save();
        }

        float scale = _config.OverlayScale;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Scale##ovscale", ref scale, 0.5f, 3f, "%.2f")) {
            _config.OverlayScale = scale;
            _config.Save();
        }

        float ox = _config.OverlayX, oy = _config.OverlayY;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputFloat("X##ovx", ref ox)) { _config.OverlayX = ox; _config.Save(); }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputFloat("Y##ovy", ref oy)) { _config.OverlayY = oy; _config.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Action hook: GetAdjustedActionId");
        ImGui.TextDisabled("Replacements active only while flows are enabled.");
    }
}
