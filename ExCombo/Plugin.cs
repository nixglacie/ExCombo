using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
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
    [PluginService] internal static IDataManager            DataManager        { get; private set; } = null!;

    private readonly Configuration    _config;
    private readonly WindowSystem     _windowSystem = new("ExCombo");
    private readonly MainWindow       _mainWindow;
    private readonly FlowEditorWindow _editorWindow;
    private readonly ConfigWindow     _configWindow;
    private readonly IDtrBarEntry     _dtrEntry;
    private readonly ActionHook       _actionHook;

    private const string Command = "/excombo";

    public Plugin() {
        _config     = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _actionHook = new ActionHook(_config);

        _editorWindow = new FlowEditorWindow(_config);
        _configWindow = new ConfigWindow(_config);
        _mainWindow   = new MainWindow(_config, _editorWindow, _configWindow);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_editorWindow);
        _windowSystem.AddWindow(_configWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand) {
            HelpMessage = "Open ExCombo rotation flow editor.",
        });

        _dtrEntry         = DtrBar.Get("ExCombo");
        _dtrEntry.Text    = "EX";
        _dtrEntry.Tooltip = new Dalamud.Game.Text.SeStringHandling.SeString(
            new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload("ExCombo"));
        _dtrEntry.OnClick = _ => _mainWindow.Toggle();
        _dtrEntry.Shown   = _config.ShowDtrEntry;

        PluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _mainWindow.Toggle;
        Framework.Update                        += OnFrameworkUpdate;
    }

    private void OnCommand(string cmd, string args) => _mainWindow.Toggle();

    private void OnFrameworkUpdate(IFramework _) {
        _dtrEntry.Shown = _config.ShowDtrEntry;
    }

    public void Dispose() {
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
