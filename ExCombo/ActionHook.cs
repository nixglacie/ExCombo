using System;
using Dalamud.Hooking;
using ExCombo.Flow;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal sealed class ActionHook : IDisposable {
    private delegate uint  GetAdjustedActionIdDelegate(IntPtr actionManager, uint actionId);
    private delegate bool  UseActionDelegate(IntPtr actionManager, uint actionType, uint actionId, ulong targetId, uint a4, uint a5, uint a6, IntPtr a7);
    private delegate ulong IsActionReplaceableDelegate(uint actionId);

    private readonly Hook<GetAdjustedActionIdDelegate>  _hook;
    private readonly Hook<UseActionDelegate>            _useHook;
    private readonly Hook<IsActionReplaceableDelegate>  _isReplaceableHook;
    private readonly Configuration                      _config;
    private readonly System.Collections.Generic.Dictionary<uint, uint> _lastIcon = new();

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

    // Called every frame per hotbar slot, and inside UseAction(a5=1) for the queued action.
    private uint Detour(IntPtr actionManager, uint actionId) {
        try {
            foreach (var flow in _config.Flows) {
                if (!flow.Enabled) continue;
                foreach (var trigger in flow.Nodes) {
                    if (trigger.Type != NodeType.Trigger) continue;
                    if (trigger.ActionId == 0 || trigger.ActionId != actionId) continue;
                    var r       = FlowExecutor.Resolve(flow, trigger, actionId);
                    var evolved = _hook.Original(actionManager, r);
                    if (!_lastIcon.TryGetValue(actionId, out var prev) || prev != evolved) {
                        Plugin.Log.Debug($"[ExCombo][Icon] slot={actionId} → {evolved}");
                        _lastIcon[actionId] = evolved;
                    }
                    return evolved;
                }
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "ActionHook detour error");
        }
        return _hook.Original(actionManager, actionId);
    }

    private unsafe bool UseActionDetour(IntPtr actionManager, uint actionType, uint actionId, ulong targetId, uint a4, uint a5, uint a6, IntPtr a7) {
        if (a5 == 1) FlowExecutor.InQueueExecute = true;

        // Retarget: if the action about to fire belongs to a flow Action node flagged with a built-in
        // resolver, redirect the cast target without changing the player's hard target.
        if (actionType == (uint)ActionType.Action) {
            try {
                foreach (var flow in _config.Flows) {
                    if (!flow.Enabled) continue;
                    foreach (var trigger in flow.Nodes) {
                        if (trigger.Type != NodeType.Trigger) continue;
                        var mode = FlowExecutor.GetRetargetForUsedAction(flow, trigger, actionId);
                        if (mode == 0) continue;
                        var resolved = Helpers.RetargetResolver.Resolve((RetargetMode)mode);
                        if (resolved is { } tid && tid != 0) {
                            Plugin.Log.Debug($"[ExCombo][Retarget] id={actionId} mode={(RetargetMode)mode} → {tid}");
                            targetId = tid;
                        }
                        goto resolved_done;
                    }
                }
                resolved_done: ;
            } catch (Exception ex) {
                Plugin.Log.Error(ex, "ActionHook retarget error");
            }
        }

        var result = _useHook.Original(actionManager, actionType, actionId, targetId, a4, a5, a6, a7);
        FlowExecutor.InQueueExecute = false;
        Plugin.Log.Debug($"[ExCombo][UseAction] id={actionId} a5={a5} result={result}");

        if (!result) {
            if (a5 == 1) {
                // Queue fired but failed (e.g. out of range) — unfreeze so player can retry
                try {
                    foreach (var flow in _config.Flows) {
                        if (!flow.Enabled) continue;
                        foreach (var trigger in flow.Nodes) {
                            if (trigger.Type != NodeType.Trigger) continue;
                            FlowExecutor.NotifyQueueFailed(flow, trigger);
                        }
                    }
                } catch (Exception ex) {
                    Plugin.Log.Error(ex, "ActionHook queue fail error");
                }
            }
            return false;
        }

        try {
            foreach (var flow in _config.Flows) {
                if (!flow.Enabled) continue;
                foreach (var trigger in flow.Nodes) {
                    if (trigger.Type != NodeType.Trigger) continue;
                    if (a5 == 0) {
                        // a5=0: only the exact button pressed should advance its trigger.
                        if (trigger.ActionId != actionId && _hook.Original(actionManager, trigger.ActionId) != actionId) continue;
                        var current = FlowExecutor.GetCurrentChainAction(flow, trigger);
                        var isQueued = ActionManager.Instance()->ActionQueued;
                        if (isQueued) {
                            FlowExecutor.NotifyQueued(flow, trigger, current);
                        } else {
                            FlowExecutor.NotifyPressed(flow, trigger);
                        }
                    } else {
                        // a5=1: match by trigger id OR current chain action (including evolved IDs).
                        var current = FlowExecutor.GetCurrentChainAction(flow, trigger);
                        if (trigger.ActionId != actionId && current != actionId && _hook.Original(actionManager, current) != actionId) continue;
                        FlowExecutor.NotifyFired(flow, trigger);
                    }
                }
            }
        } catch (Exception ex) {
            Plugin.Log.Error(ex, "ActionHook UseAction error");
        }

        return true;
    }
}
