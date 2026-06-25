using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ExCombo.Helpers;

public struct RecastInfo {
    public float  RecastTime;
    public float  RecastTimeElapsed;
    public ushort MaxCharges;
    public RecastInfo(float t, float e, ushort c) { RecastTime = t; RecastTimeElapsed = e; MaxCharges = c; }
}

public unsafe class ActionHelpers {
    private readonly ActionManager* _mgr;

    internal static readonly Dictionary<Job, uint> JobActionIDs = new() {
        [Job.GNB] = 16137, [Job.WAR] = 31,    [Job.MRD] = 31,    [Job.DRK] = 3617,
        [Job.PLD] = 9,     [Job.GLA] = 9,      [Job.SCH] = 163,   [Job.AST] = 3596,
        [Job.WHM] = 119,   [Job.CNJ] = 119,    [Job.SGE] = 24283, [Job.BRD] = 97,
        [Job.ARC] = 97,    [Job.DNC] = 15989,  [Job.MCH] = 2866,  [Job.SMN] = 163,
        [Job.ACN] = 163,   [Job.RDM] = 7504,   [Job.BLM] = 142,   [Job.THM] = 142,
        [Job.SAM] = 7477,  [Job.NIN] = 2240,   [Job.ROG] = 2240,  [Job.MNK] = 53,
        [Job.PGL] = 53,    [Job.DRG] = 75,     [Job.LNC] = 75,    [Job.RPR] = 24373,
        [Job.BLU] = 11385,
    };

    public ActionHelpers() { _mgr = ActionManager.Instance(); }

    public uint GetAdjustedActionId(uint id) => _mgr->GetAdjustedActionId(id);

    public bool CanUseAction(uint id, ActionType type = ActionType.Action, ulong target = 0xE000_0000) =>
        _mgr->GetActionStatus(type, id, target, false, true) == 0;

    public bool IsActionHighlighted(uint id) =>
        _mgr->IsActionHighlighted(ActionType.Action, id);

    public uint  GetLastUsedActionId() => _mgr->Combo.Action;
    public float GetAnimationLock()    => _mgr->AnimationLock;

    public bool ActionInRange(uint actionId) {
        IGameObject? player = Plugin.ObjectTable.LocalPlayer;
        IGameObject? target = Plugin.TargetManager.SoftTarget ?? Plugin.TargetManager.Target;
        if (player == null || target == null) return false;
        // 0=ok, 565=in-range but not facing, 566=out of range, 562=not in LoS
        uint r = ActionManager.GetActionInRangeOrLoS(actionId,
            (GameObject*)player.Address, (GameObject*)target.Address);
        return r != 566;
    }

    public bool TargetInLoS(uint actionId) {
        IGameObject? player = Plugin.ObjectTable.LocalPlayer;
        IGameObject? target = Plugin.TargetManager.SoftTarget ?? Plugin.TargetManager.Target;
        if (player == null || target == null) return false;
        uint r = ActionManager.GetActionInRangeOrLoS(actionId,
            (GameObject*)player.Address, (GameObject*)target.Address);
        return r != 562;
    }

    public void GetAdjustedRecastInfo(uint id, out RecastInfo info) {
        info = default;
        int grp = _mgr->GetRecastGroup((int)ActionType.Action, id);
        RecastDetail* det = _mgr->GetRecastGroupDetail(grp);
        if (det == null) return;
        info.RecastTime        = det->Total;
        info.RecastTimeElapsed = det->Elapsed;
        info.MaxCharges        = ActionManager.GetMaxCharges(id, 100);
        if (info.MaxCharges == 1) return;
        ushort cur = ActionManager.GetMaxCharges(id, 0);
        if (cur == info.MaxCharges) return;
        info.RecastTime = (info.RecastTime * cur) / info.MaxCharges;
        info.MaxCharges = cur;
        if (info.RecastTimeElapsed > info.RecastTime) { info.RecastTime = 0; info.RecastTimeElapsed = 0; }
    }

    public static void GetGCDInfo(out RecastInfo info) {
        if (!JobActionIDs.TryGetValue(CharacterState.GetCharacterJob(), out uint id)) {
            info = new(0, 0, 0); return;
        }
        var h = Plugin.Actions;
        h.GetAdjustedRecastInfo(h.GetAdjustedActionId(id), out info);
    }
}
