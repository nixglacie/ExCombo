using System;
using Dalamud.Hooking;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal sealed class ActionHook : IDisposable {
    private delegate uint GetAdjustedActionIdDelegate(IntPtr actionManager, uint actionId);

    private readonly Hook<GetAdjustedActionIdDelegate> _hook;
    private readonly Configuration                     _config;

    public ActionHook(Configuration config) {
        _config = config;
        _hook   = Plugin.GameInteropProvider.HookFromAddress<GetAdjustedActionIdDelegate>(
            (nint)ActionManager.Addresses.GetAdjustedActionId.Value,
            Detour);
        _hook.Enable();
    }

    public void Dispose() {
        _hook.Disable();
        _hook.Dispose();
    }

    private uint Detour(IntPtr actionManager, uint actionId) {
        try {
            foreach (var flow in _config.Flows) {
                if (!flow.Enabled) continue;
                var trigger = flow.Nodes.Find(n => n.Type == NodeType.Trigger);
                if (trigger?.ActionId == actionId)
                    return FlowExecutor.Resolve(flow, actionId);
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "ActionHook detour error");
        }
        return _hook.Original(actionManager, actionId);
    }
}
