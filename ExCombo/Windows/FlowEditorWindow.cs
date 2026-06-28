using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ExCombo.Flow;

namespace ExCombo.Windows;

public class FlowEditorWindow : Window {
    private readonly Configuration _config;
    private ComboFlow? _flow;
    public string? ActiveFlowId => _flow?.Id;

    private Vector2 _canvasOffset = Vector2.Zero;

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private static uint Bg1 => Col(0.102f, 0.106f, 0.118f);
    private static uint Bg2 => Col(0.145f, 0.149f, 0.169f);

    public FlowEditorWindow(Configuration config)
        : base("Flow Editor###ExComboEditor") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
        _config = config;
    }

    public void SetFlow(ComboFlow flow) {
        _flow         = flow;
        _canvasOffset = Vector2.Zero;
        WindowName    = $"Flow Editor — {flow.Name}###ExComboEditor";
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        if (_flow == null) {
            ImGui.TextDisabled("No flow selected.");
            return;
        }

        var dl        = ImGui.GetWindowDrawList();
        var canvasMin = ImGui.GetCursorScreenPos();
        var canvasMax = canvasMin + ImGui.GetContentRegionAvail();
        var canvasSize = canvasMax - canvasMin;

        // Background
        dl.AddRectFilled(canvasMin, canvasMax, Bg1);
        dl.AddRect(canvasMin, canvasMax, Col(0.333f, 0.353f, 0.388f));

        // Grid
        const float GridStep = 32f;
        uint gridCol = Col(0.173f, 0.180f, 0.200f, 0.6f);
        for (float x = (_canvasOffset.X % GridStep); x < canvasSize.X; x += GridStep)
            dl.AddLine(new Vector2(canvasMin.X + x, canvasMin.Y),
                       new Vector2(canvasMin.X + x, canvasMax.Y), gridCol);
        for (float y = (_canvasOffset.Y % GridStep); y < canvasSize.Y; y += GridStep)
            dl.AddLine(new Vector2(canvasMin.X, canvasMin.Y + y),
                       new Vector2(canvasMax.X, canvasMin.Y + y), gridCol);

        // Invisible overlay to capture input
        ImGui.InvisibleButton("##canvas", canvasSize,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();

        // Middle-mouse pan
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) {
            _canvasOffset += ImGui.GetIO().MouseDelta;
        }

        // Empty hint
        dl.AddText(canvasMin + new Vector2(12f, 12f),
            Col(0.333f, 0.353f, 0.388f), "Right-click to add nodes");
    }
}
