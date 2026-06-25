using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ExCombo.Flow;
using ExCombo.Helpers;
using ExCombo.Hooks;
using ExCombo.Windows;

namespace ExCombo;

public sealed class Plugin : IDalamudPlugin {
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition       { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager     { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider   GameInterop     { get; private set; } = null!;
    [PluginService] internal static IPlayerState           PlayerState     { get; private set; } = null!;
    [PluginService] internal static ITextureProvider       TextureProvider { get; private set; } = null!;
    [PluginService] internal static IJobGauges             JobGauges       { get; private set; } = null!;
    [PluginService] internal static ITargetManager         TargetManager   { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;

    internal static ActionHelpers  Actions  { get; private set; } = null!;
    internal static StatusHelpers  Statuses { get; private set; } = null!;
    internal static TexturesCache  Textures { get; private set; } = null!;

    // Cached on game thread (Framework.Update), read on render thread (Draw)
    internal static uint? OverlayNextAction;
    internal static bool  OverlayNextUsable;
    internal static bool  OverlayInCombat;

    private readonly Configuration    _config;
    private readonly FlowExecutor     _executor;
    private readonly ActionHook       _actionHook;
    private readonly WindowSystem     _windowSystem = new("ExCombo");
    private readonly MainWindow       _mainWindow;
    private readonly FlowEditorWindow _editorWindow;
    private readonly ConfigWindow     _configWindow;
    private readonly OverlayWindow    _overlayWindow;

    private const string Command = "/excombo";

    public Plugin() {
        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Actions  = new ActionHelpers();
        Statuses = new StatusHelpers();
        Textures = new TexturesCache();

        _executor   = new FlowExecutor(_config);
        _actionHook = new ActionHook(GameInterop, _executor, Log);

        _editorWindow  = new FlowEditorWindow(_config, DataManager);
        _configWindow  = new ConfigWindow(_config);
        _mainWindow    = new MainWindow(_config, _editorWindow, _configWindow);
        _overlayWindow = new OverlayWindow(_config);

        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_editorWindow);
        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_overlayWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand) {
            HelpMessage = "Open ExCombo rotation flow editor.",
        });

        PluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _mainWindow.Toggle;
        Framework.Update                        += OnFrameworkUpdate;
    }

    private void OnCommand(string cmd, string args) => _mainWindow.Toggle();

    private void OnFrameworkUpdate(IFramework _) {
        Statuses.GenerateStatusMap();
        OverlayInCombat   = CharacterState.IsInCombat();
        var next          = OverlayInCombat ? _executor.PeekNext() : null;
        OverlayNextAction = next;
        OverlayNextUsable = next.HasValue && Actions.CanUseAction(next.Value);
    }

    public void Dispose() {
        Framework.Update                        -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw          -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi  -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi    -= _mainWindow.Toggle;
        CommandManager.RemoveHandler(Command);
        _windowSystem.RemoveAllWindows();
        _actionHook.Dispose();
        Textures.Dispose();
    }
}
