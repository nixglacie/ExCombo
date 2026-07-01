using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ExCombo.Windows;

// Live runtime overlay: what each enabled trigger is about to fire, weave budget, branch state.
public class DebugWindow : Window {
    private readonly Configuration _config;

    public DebugWindow(Configuration config) : base("ExCombo Debug###ExComboDebug") {
        _config = config;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(460, 180),
            MaximumSize = new Vector2(900, 700),
        };
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    public override void Draw() {
        var suppressed = FlowExecutor.ReplacementSuppressed();
        ImGui.TextColored(
            suppressed ? new Vector4(1f, 0.55f, 0.35f, 1f) : new Vector4(0.55f, 0.85f, 0.55f, 1f),
            suppressed ? "Replacement SUPPRESSED (master off or safety gate)" : "Active");
        ImGui.Spacing();

        var rows = FlowExecutor.Snapshot(_config.Flows);
        if (rows.Count == 0) {
            ImGui.TextDisabled("No enabled flows.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##dbg", 7, flags)) return;

        ImGui.TableSetupColumn("Flow");
        ImGui.TableSetupColumn("Trigger");
        ImGui.TableSetupColumn("Next");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Weave");
        ImGui.TableSetupColumn("Port");
        ImGui.TableSetupColumn("Spine");
        ImGui.TableHeadersRow();

        foreach (var r in rows) {
            ImGui.TableNextRow();

            ImGui.TableNextColumn(); ImGui.Text(r.FlowName);
            ImGui.TableNextColumn(); ImGui.Text(r.TriggerLabel);

            ImGui.TableNextColumn();
            if (!r.HasState) ImGui.TextDisabled("idle");
            else             ImGui.Text($"{r.NextLabel} ({r.NextActionId})");

            ImGui.TableNextColumn();
            if      (!r.HasState) ImGui.TextDisabled("—");
            else if (r.Pending)   ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), "queued");
            else if (r.Committed) ImGui.TextColored(new Vector4(0.70f, 0.55f, 1f, 1f), "committed");
            else                  ImGui.Text("ready");

            ImGui.TableNextColumn();
            var wc = r.WeaveOpen ? new Vector4(0.55f, 0.85f, 0.55f, 1f) : new Vector4(0.6f, 0.6f, 0.6f, 1f);
            ImGui.TextColored(wc, $"{r.Weaved}/{r.MaxWeaves}{(r.WeaveOpen ? " open" : "")}");

            ImGui.TableNextColumn();
            ImGui.Text(r.BranchPort >= 0 ? r.BranchPort.ToString() : "—");

            ImGui.TableNextColumn();
            ImGui.Text(r.Spine != 0 ? r.Spine.ToString() : "—");
        }

        ImGui.EndTable();
    }
}
