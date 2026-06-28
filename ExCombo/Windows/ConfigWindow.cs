using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ExCombo.Windows;

public class ConfigWindow : Window {
    private readonly Configuration _config;

    public ConfigWindow(Configuration config) : base("ExCombo Settings###ExComboConfig") {
        _config = config;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(300, 120),
            MaximumSize = new Vector2(500, 300),
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
        ImGui.Text("Server Bar");
        ImGui.Spacing();

        bool dtr = _config.ShowDtrEntry;
        if (ImGui.Checkbox("Show \"EX\" in server info bar", ref dtr)) {
            _config.ShowDtrEntry = dtr;
            _config.Save();
        }
    }
}
