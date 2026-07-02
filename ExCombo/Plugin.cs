using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ExCombo.Windows;

namespace ExCombo;

public sealed class Plugin : IDalamudPlugin {
    [PluginService] internal static IDalamudPluginInterface PluginInterface    { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager     { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider    { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log                { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework          { get; private set; } = null!;
    [PluginService] internal static IDtrBar                 DtrBar             { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager         { get; private set; } = null!;
    [PluginService] internal static IJobGauges              JobGauges           { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState         { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager       { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable         { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition           { get; private set; } = null!;
    [PluginService] internal static IPartyList              PartyList           { get; private set; } = null!;
    [PluginService] internal static IKeyState               KeyState            { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui             { get; private set; } = null!;

    // Shared config handle for the static runtime (ActionHook / FlowExecutor / WeaveHelper).
    internal static Configuration Config { get; private set; } = null!;

    // Level-gated debug log — silenced unless LogLevel.Verbose. Errors bypass this (call Log.Error).
    internal static void LogDebug(string msg) {
        if (Config is { LogLevel: LogLevel.Verbose }) Log.Debug(msg);
    }

    private readonly Configuration    _config;
    private readonly WindowSystem     _windowSystem = new("ExCombo");
    private readonly MainWindow       _mainWindow;
    private readonly FlowEditorWindow _editorWindow;
    private readonly ConfigWindow     _configWindow;
    private readonly DebugWindow      _debugWindow;
    private readonly NodeWikiWindow   _wikiWindow;
    private readonly IDtrBarEntry     _dtrEntry;
    private readonly ActionHook       _actionHook;

    private const string Command = "/excombo";

    public Plugin() {
        _config     = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config      = _config;
        MigrateConfig(_config);
        _actionHook = new ActionHook(_config);

        _wikiWindow   = new NodeWikiWindow();
        _editorWindow = new FlowEditorWindow(_config, _wikiWindow);
        _debugWindow  = new DebugWindow(_config);
        _configWindow = new ConfigWindow(_config, _debugWindow);
        _mainWindow   = new MainWindow(_config, _editorWindow, _configWindow);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_editorWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_debugWindow);
        _windowSystem.AddWindow(_wikiWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand) {
            HelpMessage = "Open ExCombo rotation flow editor. \"/excombo wiki\" opens the node reference; "
                        + "\"/excombo toggle <name>\" flips a Toggle node.",
        });

        _dtrEntry         = DtrBar.Get("ExCombo");
        _dtrEntry.Text    = "EX";
        _dtrEntry.Tooltip = new SeString(
            new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(
                "ExCombo\nLeft click: toggle on/off\nRight click: open flow list"));
        _dtrEntry.OnClick = e => {
            if (e.ClickType == MouseClickType.Right) {
                _mainWindow.Toggle();
            } else {
                _config.Enabled = !_config.Enabled;
                _config.Save();
            }
        };
        _dtrEntry.Shown   = _config.ShowDtrEntry;

        PluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _mainWindow.Toggle;
        Framework.Update                        += OnFrameworkUpdate;
    }

    // One-time config migrations, keyed off Configuration.Version.
    private static void MigrateConfig(Configuration cfg) {
        if (cfg.Version < 2) {
            // Legacy job-gauge gate (NodeType.Condition) → unified GaugeCondition family. The gauge
            // field name moves ConditionField → CheckField; compare op/value are already shared.
            foreach (var flow in cfg.Flows)
                foreach (var node in flow.Nodes)
                    if (node.Type == Flow.NodeType.Condition) {
                        node.Type       = Flow.NodeType.GaugeCondition;
                        node.CheckField = node.ConditionField;
                    }
            cfg.Version = 2;
            cfg.Save();
        }
        if (cfg.Version < 3) {
            // PlayerHasAggro moved from the Party family to Player (it's the player's own threat).
            foreach (var flow in cfg.Flows)
                foreach (var node in flow.Nodes)
                    if (node.Type == Flow.NodeType.PartyCondition && node.CheckField == "PlayerHasAggro")
                        node.Type = Flow.NodeType.PlayerCondition;
            cfg.Version = 3;
            cfg.Save();
        }
    }

    private void OnCommand(string cmd, string args) {
        var a = args.Trim();
        if (a.Equals("wiki", System.StringComparison.OrdinalIgnoreCase)) {
            _wikiWindow.Toggle();
            return;
        }
        if (a.StartsWith("toggle ", System.StringComparison.OrdinalIgnoreCase)) {
            ToggleByName(a["toggle ".Length..].Trim());
            return;
        }
        _mainWindow.Toggle();
    }

    // Flip every Toggle node whose name matches, across all flows. Persists and prints feedback.
    // Echo is client-side only (IChatGui.Print) — never sent to other players.
    private void ToggleByName(string name) {
        if (name.Length == 0) { ChatGui.Print("usage: /excombo toggle <name>", "ExCombo"); return; }
        var hits = 0;
        bool? newState = null;
        foreach (var flow in _config.Flows) {
            var touched = false;
            foreach (var node in flow.Nodes) {
                if (node.Type != Flow.NodeType.ToggleCondition) continue;
                if (!node.ActionLabel.Equals(name, System.StringComparison.OrdinalIgnoreCase)) continue;
                node.ToggleOn = newState ??= !node.ToggleOn;   // first hit decides; rest sync to it
                touched = true;
                hits++;
            }
            if (touched) FlowExecutor.InvalidateFlow(flow.Id);
        }
        if (hits > 0) {
            _config.Save();
            ChatGui.Print($"\"{name}\" → {(newState == true ? "ON" : "OFF")}"
                        + (hits > 1 ? $" ({hits} nodes)" : ""), "ExCombo");
        } else {
            ChatGui.Print($"no Toggle node named \"{name}\" found.", "ExCombo");
        }
    }

    private void OnFrameworkUpdate(IFramework _) {
        UpdateDtr();
        FlowExecutor.Tick(_config.Flows);
    }

    private string? _dtrLastKey;

    // Server-bar text: "EX", optionally with master On/Off state and/or job icon +
    // count of enabled flows for the player's current job. Rebuilt each tick, assigned only on change.
    private void UpdateDtr() {
        _dtrEntry.Shown = _config.ShowDtrEntry;
        if (!_config.ShowDtrEntry) return;

        bool showOff = _config.DtrShowState && !_config.Enabled;

        var  flowText = "";
        uint jobRow   = 0;
        if (_config.DtrShowActiveFlow && _config.Enabled) {
            var player = ObjectTable.LocalPlayer;
            var job    = player?.ClassJob.ValueNullable?.Abbreviation.ToString();
            if (!string.IsNullOrEmpty(job)) {
                var count = _config.Flows.Count(f => f.Enabled && f.Job == job);
                if (count > 0) {
                    jobRow   = player!.ClassJob.RowId;
                    flowText = count.ToString();
                }
            }
        }

        var key = $"{showOff}|{jobRow}|{flowText}";
        if (key == _dtrLastKey) return;
        _dtrLastKey = key;

        if (showOff) {
            _dtrEntry.Text = new SeStringBuilder().AddText("EX: ")
                .AddUiForeground("Off", 539).Build();   // UIColor 539 = red
            return;
        }

        if (flowText.Length == 0) {
            _dtrEntry.Text = "EX";
            return;
        }

        var sb = new SeStringBuilder().AddText("EX: ");
        var icon = JobBitmapIcon(jobRow);
        if (icon != BitmapFontIcon.None) sb.AddIcon(icon);
        sb.AddText(flowText);
        _dtrEntry.Text = sb.Build();
    }

    // ClassJob row → BitmapFontIcon: rows 1–40 are contiguous at 127+row; VPR/PCT skip ahead.
    private static BitmapFontIcon JobBitmapIcon(uint rowId) => rowId switch {
        >= 1 and <= 40 => (BitmapFontIcon)(127 + rowId),
        41             => BitmapFontIcon.Viper,
        42             => BitmapFontIcon.Pictomancer,
        _              => BitmapFontIcon.None,
    };

    public void Dispose() {
        _editorWindow.Dispose();
        _wikiWindow.Dispose();
        _actionHook.Dispose();
        _dtrEntry.Remove();
        Framework.Update                       -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _mainWindow.Toggle;
        CommandManager.RemoveHandler(Command);
        _windowSystem.RemoveAllWindows();
    }
}
