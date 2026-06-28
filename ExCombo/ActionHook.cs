using System;
using Dalamud.Hooking;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal sealed class ActionHook : IDisposable {
    private delegate uint  GetAdjustedActionIdDelegate(IntPtr actionManager, uint actionId);
    private delegate bool  UseActionDelegate(IntPtr actionManager, uint actionType, uint actionId, ulong targetId, uint a4, uint a5, uint a6, IntPtr a7);
    private delegate ulong IsActionReplaceableDelegate(uint actionId);

    private readonly Hook<GetAdjustedActionIdDelegate> _hook;
    private readonly Hook<UseActionDelegate>           _useHook;
    private readonly Hook<IsActionReplaceableDelegate> _isReplaceableHook;
    private readonly Configuration                     _config;

    public ActionHook(Configuration config) {
        _config = config;

        _hook = Plugin.GameInteropProvider.HookFromAddress<GetAdjustedActionIdDelegate>(
            (nint)ActionManager.Addresses.GetAdjustedActionId.Value, Detour);
        _hook.Enable();

        _useHook = Plugin.GameInteropProvider.HookFromAddress<UseActionDelegate>(
            (nint)ActionManager.Addresses.UseAction.Value, UseActionDetour);
        _useHook.Enable();

        _isReplaceableHook = Plugin.GameInteropProvider.HookFromSignature<IsActionReplaceableDelegate>(
            "40 53 48 83 EC 20 8B D9 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 1F",
            _ => 1UL);
        _isReplaceableHook.Enable();
    }

    public void Dispose() {
        _isReplaceableHook.Disable();
        _isReplaceableHook.Dispose();
        _useHook.Disable();
        _useHook.Dispose();
        _hook.Disable();
        _hook.Dispose();
    }

    // Called every frame per hotbar slot, and also inside UseAction before the action fires.
    // Returns which action ID the hotbar/game should use for this actionId.
    private uint Detour(IntPtr actionManager, uint actionId) {
        try {
            foreach (var flow in _config.Flows) {
                if (!flow.Enabled) continue;
                foreach (var trigger in flow.Nodes) {
                    if (trigger.Type != NodeType.Trigger) continue;
                    if (trigger.ActionId == 0 || trigger.ActionId != actionId) continue;
                    return FlowExecutor.Resolve(flow, trigger, actionId);
                }
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "ActionHook detour error");
        }
        return _hook.Original(actionManager, actionId);
    }

    private bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionId, ulong targetId, uint a4, uint a5, uint a6, IntPtr a7) {
        // a5=0: button press — action executes immediately OR is queued for end-of-GCD.
        // a5=1: queued action fires — GetAdjustedActionId is called again inside the original.
        //       By then Index has already advanced from a5=0, so without the fix GetAdj would
        //       return the NEXT action instead of the queued one. InQueueExecute tells Resolve
        //       to return QueuedAction (saved at a5=0) instead.
        if (a5 == 1) FlowExecutor.InQueueExecute = true;

        var result = _useHook.Original(actionManager, actionType, actionId, targetId, a4, a5, a6, a7);

        FlowExecutor.InQueueExecute = false;

        Plugin.Log.Debug($"[ExCombo][UseAction] id={actionId} a5={a5} result={result}");

        if (!result) return false;

        try {
            foreach (var flow in _config.Flows) {
                if (!flow.Enabled) continue;
                foreach (var trigger in flow.Nodes) {
                    if (trigger.Type != NodeType.Trigger) continue;
                    var current = FlowExecutor.GetCurrentChainAction(flow, trigger);
                    if (trigger.ActionId != actionId && current != actionId) continue;

                    if (a5 == 0) {
                        // Advance index now. Save current action in case this press queued it
                        // (a5=1 will fire later and must return the queued action, not the new index).
                        FlowExecutor.NotifyPressed(flow, trigger);
                        FlowExecutor.SaveQueuedAction(flow, trigger, current);
                    } else {
                        // Queue fired: correct action already executed via QueuedAction. Just clear it.
                        FlowExecutor.ClearQueuedAction(flow, trigger);
                    }
                }
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "ActionHook UseAction error");
        }

        return true;
    }
}
