using System;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ExCombo.Windows;

public class ConfigWindow : Window {
    private readonly Configuration _config;
    private readonly DebugWindow   _debug;
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public ConfigWindow(Configuration config, DebugWindow debug) : base("ExCombo Settings###ExComboConfig") {
        _config = config;
        _debug  = debug;
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(540, 320),
            MaximumSize = new Vector2(680, 680),
        };
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    private static readonly string[] Tabs = { "General", "Behavior", "Editor", "Theme", "Debug", "About" };
    private int _tab;

    public override void Draw() {
        // Pill tabs (match the action picker's PvE/PvP buttons).
        for (var i = 0; i < Tabs.Length; i++) {
            if (i > 0) ImGui.SameLine(0, 6f);
            DrawTabButton(Tabs[i], _tab == i, () => _tab = i);
        }
        ImGui.Spacing();
        ImGui.Separator();

        switch (_tab) {
            case 0: DrawGeneral();  break;
            case 1: DrawBehavior(); break;
            case 2: DrawEditor();   break;
            case 3: DrawTheme();    break;
            case 4: DrawDebug();    break;
            case 5: DrawAbout();    break;
        }
    }

    // Rounded pill tab button, accent-filled when active (mirrors FlowEditorWindow's picker tabs).
    private static void DrawTabButton(string label, bool active, Action onClick) {
        var accent    = Style.AccentColor;
        var accentHov = Style.AccentHover;
        var accentAct = Style.AccentActive;
        var bg3       = new Vector4(0.173f, 0.180f, 0.200f, 1f);
        var bg3Hov    = new Vector4(0.216f, 0.224f, 0.247f, 1f);
        var textDark  = new Vector4(0.102f, 0.106f, 0.118f, 1f);
        var textDim   = new Vector4(0.565f, 0.573f, 0.588f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button,        active ? accent    : bg3);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? accentHov : bg3Hov);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  active ? accentAct : bg3Hov);
        ImGui.PushStyleColor(ImGuiCol.Text,          active ? textDark  : textDim);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,  new Vector2(16f, 5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 20f);
        if (ImGui.Button(label)) onClick();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
    }

    // ── General ──────────────────────────────────────────────────────────
    private void DrawGeneral() {
        ImGui.Spacing();
        bool master = _config.Enabled;
        if (ImGui.Checkbox("Enable ExCombo", ref master)) { _config.Enabled = master; _config.Save(); }
        Help("Master switch. When off, all hotbars revert to vanilla icons instantly without touching per-flow toggles.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Server Bar");
        ImGui.Spacing();

        bool dtr = _config.ShowDtrEntry;
        if (ImGui.Checkbox("Show \"EX\" in server info bar", ref dtr)) { _config.ShowDtrEntry = dtr; _config.Save(); }
        Help("Show a clickable EX button in the server info bar that opens the flow list.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Safety");
        ImGui.Spacing();

        bool pvp = _config.DisableInPvP;
        if (ImGui.Checkbox("Disable in PvP", ref pvp)) { _config.DisableInPvP = pvp; _config.Save(); }
        Help("Revert to vanilla icons while in PvP zones.");

        bool occ = _config.PauseWhenOccupied;
        if (ImGui.Checkbox("Pause during cutscenes / occupied", ref occ)) { _config.PauseWhenOccupied = occ; _config.Save(); }
        Help("Suspend replacement while occupied — cutscenes, crafting, gathering, mounting, zoning.");

        bool combatOnly = _config.ReplaceOnlyInCombat;
        if (ImGui.Checkbox("Only replace in combat", ref combatOnly)) { _config.ReplaceOnlyInCombat = combatOnly; _config.Save(); }
        Help("Keep vanilla hotbars out of combat; replace only once you're fighting.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Data");
        ImGui.Spacing();

        if (ImGui.Button("Export all flows")) {
            try { ImGui.SetClipboardText(JsonSerializer.Serialize(_config.Flows)); _dataMsg = "Copied all flows to clipboard."; }
            catch { _dataMsg = "Export failed."; }
        }
        Help("Copy every flow to the clipboard as a JSON backup. Import individual flows from the main window.");
        ImGui.SameLine();
        if (ImGui.Button("Reset all settings")) {
            ResetAllSettings();
            _dataMsg = "All settings reset (flows kept).";
        }
        Help("Restore every setting (behaviour, safety, editor, theme, debug) to defaults. Your flows are not touched.");
        if (_dataMsg != null) { ImGui.Spacing(); ImGui.TextDisabled(_dataMsg); }
    }

    private string? _dataMsg;

    // Reset all tunables to defaults; leaves Flows (and Version) alone.
    private void ResetAllSettings() {
        _config.Enabled            = true;
        _config.ShowDtrEntry       = true;
        _config.MaxWeavesPerGcd    = 2;
        _config.AnimLockBudget     = 0.6f;
        _config.QueueBudget        = 0.5f;
        _config.ComboGraceMs       = 500;
        _config.ChainResetSeconds  = 15;
        _config.DisableInPvP       = false;
        _config.PauseWhenOccupied  = true;
        _config.ReplaceOnlyInCombat= false;
        _config.GridSize           = 32f;
        _config.SnapToGrid         = true;
        _config.ConfirmNodeDelete  = false;
        _config.UndoDepth          = 50;
        _config.WireStyle          = WireStyle.Curved;
        _config.AccentColor        = new[] { 0.455f, 0.765f, 1.0f, 1f };
        _config.LogLevel           = LogLevel.Verbose;
        _config.ShowConditionState = false;
        _config.Save();
    }

    // ── Theme ────────────────────────────────────────────────────────────
    private void DrawTheme() {
        ImGui.Spacing();
        var acc = _config.AccentColor;
        var col = new Vector3(acc.Length > 0 ? acc[0] : 0.455f,
                              acc.Length > 1 ? acc[1] : 0.765f,
                              acc.Length > 2 ? acc[2] : 1.0f);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.ColorEdit3("Accent color", ref col)) {
            _config.AccentColor = new[] { col.X, col.Y, col.Z, 1f };
            _config.Save();
        }
        Help("Primary interactive color — buttons, checkmarks, sliders, highlights across all ExCombo windows.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Node colors");
        Help("Colors of each node type in the flow editor (borders, labels, ports). Selection uses the accent color.");
        ImGui.Spacing();
        NodeColorRow("Trigger",   _config.NodeColorTrigger,   a => { _config.NodeColorTrigger   = a; _config.Save(); });
        NodeColorRow("Action",    _config.NodeColorAction,    a => { _config.NodeColorAction    = a; _config.Save(); });
        NodeColorRow("Priority",  _config.NodeColorBranch,    a => { _config.NodeColorBranch    = a; _config.Save(); });
        NodeColorRow("Condition", _config.NodeColorCondition, a => { _config.NodeColorCondition = a; _config.Save(); });
        NodeColorRow("Note",      _config.NodeColorNote,      a => { _config.NodeColorNote      = a; _config.Save(); });
        NodeColorRow("Combo group", _config.ComboGroupColor,  a => { _config.ComboGroupColor    = a; _config.Save(); });

        ImGui.Spacing();
        ImGui.Text("Badges");
        Help("Status badges shown on action nodes: oGCD (lightning), retarget (crosshairs). Combo-group badge uses the combo group color.");
        ImGui.Spacing();
        NodeColorRow("oGCD badge",     _config.BadgeOgcdColor,     a => { _config.BadgeOgcdColor     = a; _config.Save(); });
        NodeColorRow("Combo badge",    _config.BadgeComboColor,    a => { _config.BadgeComboColor    = a; _config.Save(); });
        NodeColorRow("Retarget badge", _config.BadgeRetargetColor, a => { _config.BadgeRetargetColor = a; _config.Save(); });

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Reset to defaults")) {
            _config.AccentColor        = new[] { 0.455f, 0.765f, 1.0f, 1f };
            _config.NodeColorTrigger   = new[] { 0.635f, 0.855f, 0.549f };
            _config.NodeColorAction    = new[] { 0.455f, 0.765f, 1.000f };
            _config.NodeColorBranch    = new[] { 0.700f, 0.400f, 1.000f };
            _config.NodeColorCondition = new[] { 0.900f, 0.630f, 0.310f };
            _config.NodeColorNote      = new[] { 1.000f, 1.000f, 1.000f };
            _config.ComboGroupColor    = new[] { 1.000f, 0.700f, 0.200f };
            _config.BadgeOgcdColor     = new[] { 1.000f, 0.850f, 0.200f };
            _config.BadgeRetargetColor = new[] { 0.400f, 0.850f, 1.000f };
            _config.BadgeComboColor    = new[] { 1.000f, 0.700f, 0.200f };
            _config.Save();
        }
    }

    private static void NodeColorRow(string label, float[] arr, Action<float[]> set) {
        var c = new Vector3(arr.Length > 0 ? arr[0] : 1f,
                            arr.Length > 1 ? arr[1] : 1f,
                            arr.Length > 2 ? arr[2] : 1f);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.ColorEdit3(label, ref c)) set(new[] { c.X, c.Y, c.Z });
    }

    // ── Editor ───────────────────────────────────────────────────────────
    private void DrawEditor() {
        ImGui.Spacing();
        int grid = (int)_config.GridSize;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Grid size (px)", ref grid, 16, 64)) {
            _config.GridSize = grid; _config.Save();
        }
        Help("Spacing of the editor grid, and the step nodes snap to. Default 32.");

        bool snap = _config.SnapToGrid;
        if (ImGui.Checkbox("Snap to grid", ref snap)) { _config.SnapToGrid = snap; _config.Save(); }
        Help("Align nodes to the grid when you finish dragging or resizing. Off = free placement.");

        bool confirm = _config.ConfirmNodeDelete;
        if (ImGui.Checkbox("Confirm node delete", ref confirm)) { _config.ConfirmNodeDelete = confirm; _config.Save(); }
        Help("Ask before deleting a node. Guards against accidental clicks (the undo button also covers you).");

        int undo = _config.UndoDepth;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Undo history", ref undo, 5, 200)) { _config.UndoDepth = undo; _config.Save(); }
        Help("How many editor steps the undo button can walk back. Undo/redo buttons sit in the editor's top-left. Default 50.");

        int wire = (int)_config.WireStyle;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Wire style", ref wire, "Curved\0Straight\0")) {
            _config.WireStyle = (WireStyle)wire; _config.Save();
        }
        Help("How node connections are drawn: curved Bézier or straight lines.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Reset to defaults")) {
            _config.GridSize          = 32f;
            _config.SnapToGrid        = true;
            _config.ConfirmNodeDelete = false;
            _config.UndoDepth         = 50;
            _config.WireStyle         = WireStyle.Curved;
            _config.Save();
        }
    }

    // ── Behavior ─────────────────────────────────────────────────────────
    private void DrawBehavior() {
        ImGui.Spacing();
        ImGui.TextDisabled("Weave & combo timing. Tune to your ping — higher ping wants larger budgets.");
        ImGui.Spacing();

        int weaves = _config.MaxWeavesPerGcd;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Max weaves / GCD", ref weaves, 1, 3)) {
            _config.MaxWeavesPerGcd = weaves; _config.Save();
        }
        Help("How many oGCDs may weave into a single GCD window. Most jobs double-weave (2); a few triple-weave.");

        float animLock = _config.AnimLockBudget;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("Anim-lock budget (s)", ref animLock, 0.3f, 1.0f, "%.2f")) {
            _config.AnimLockBudget = animLock; _config.Save();
        }
        Help("Assumed animation lock per action. Raise if oGCDs clip your GCD; lower to weave more aggressively. Default 0.60.");

        float queue = _config.QueueBudget;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderFloat("Queue lead (s)", ref queue, 0.2f, 1.0f, "%.2f")) {
            _config.QueueBudget = queue; _config.Save();
        }
        Help("How early an action may be queued before it's ready. Roughly your ping headroom. Default 0.50.");

        int grace = _config.ComboGraceMs;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Combo grace (ms)", ref grace, 100, 1500)) {
            _config.ComboGraceMs = grace; _config.Save();
        }
        Help("How long after a press before trusting the game's combo state. Covers buffs the server applies a frame late. Default 500.");

        int reset = _config.ChainResetSeconds;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Chain reset (s)", ref reset, 3, 60)) {
            _config.ChainResetSeconds = reset; _config.Save();
        }
        Help("Idle time before an unfinished combo abandons and resets to its trigger. Default 15.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Reset to defaults")) {
            _config.MaxWeavesPerGcd   = 2;
            _config.AnimLockBudget    = 0.6f;
            _config.QueueBudget       = 0.5f;
            _config.ComboGraceMs      = 500;
            _config.ChainResetSeconds = 15;
            _config.Save();
        }
    }

    // ── Debug ────────────────────────────────────────────────────────────
    private void DrawDebug() {
        ImGui.Spacing();
        bool verbose = _config.LogLevel == LogLevel.Verbose;
        if (ImGui.Checkbox("Verbose logging", ref verbose)) {
            _config.LogLevel = verbose ? LogLevel.Verbose : LogLevel.Off;
            _config.Save();
        }
        Help("When on, writes per-transition debug lines (Icon/Queue/Branch/Tick) to the Dalamud log. Off = errors only. Leave off for normal play.");

        ImGui.Spacing();
        bool inspect = _config.ShowConditionState;
        if (ImGui.Checkbox("Live condition inspector", ref inspect)) {
            _config.ShowConditionState = inspect; _config.Save();
        }
        Help("In the flow editor, tint each gate node's border green (true) or red (false) by its live evaluation. Combat only.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button(_debug.IsOpen ? "Close debug overlay" : "Open debug overlay"))
            _debug.Toggle();
        Help("Live table of every enabled trigger: next action, queue/commit state, weave budget, active branch port, GCD spine.");
    }

    // ── About ────────────────────────────────────────────────────────────
    private void DrawAbout() {
        ImGui.Spacing();
        ImGui.Text("ExCombo");
        ImGui.SameLine();
        ImGui.TextDisabled($"v{Version}");
        ImGui.Spacing();
        ImGui.TextWrapped("Node-based combat rotation editor. Use /excombo to open the flow list.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("License: AGPL-3.0-or-later");
    }

    // Small "(?)" hover marker with an explanatory tooltip.
    private static void Help(string text) {
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.40f, 0.42f, 0.46f, 1f));
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(320f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }
}
