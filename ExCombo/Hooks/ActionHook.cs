using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo.Hooks;

public sealed class ActionHook : IDisposable {
    private delegate uint GetAdjustedActionIdDelegate(nint actionManager, uint actionId);

    private Hook<GetAdjustedActionIdDelegate>? _hook;
    private readonly FlowExecutor _executor;
    [ThreadStatic] private static bool _inDetour;

    public bool Active => _hook?.IsEnabled ?? false;

    public unsafe ActionHook(IGameInteropProvider gameInterop, FlowExecutor executor, IPluginLog log) {
        _executor = executor;
        try {
            // Use FFXIVClientStructs' pre-resolved address — survives game patches automatically.
            var addr = (nint)ActionManager.Addresses.GetAdjustedActionId.Value;
            _hook = gameInterop.HookFromAddress<GetAdjustedActionIdDelegate>(addr, Detour);
            _hook.Enable();
            log.Information("[ExCombo] ActionHook active at 0x{Addr:X}", addr);
        } catch (Exception ex) {
            log.Error(ex, "[ExCombo] Failed to hook GetAdjustedActionId — combos inactive.");
        }
    }

    private uint Detour(nint actionManager, uint actionId) {
        var original = _hook!.Original(actionManager, actionId);
        // Guard against re-entry: Resolve() → ConditionEvaluator → GetAdjustedActionId → Detour again
        if (_inDetour) return original;
        _inDetour = true;
        try {
            var resolved = _executor.Resolve(original);
            if (resolved == null) return original;
            // Re-apply vanilla upgrades to ExCombo's result (e.g. Bootshine→LeapingOpo,
            // TrueStrike→RisingRaptor, SnapPunch→PouncingCoeurl based on fury stacks + level).
            // This lets flows return base action IDs and still get the upgraded action at cap.
            return _hook!.Original(actionManager, resolved.Value);
        } finally {
            _inDetour = false;
        }
    }

    public void Dispose() => _hook?.Dispose();
}
