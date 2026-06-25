using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudStatus = Dalamud.Game.ClientState.Statuses.IStatus;

namespace ExCombo.Helpers;

public enum StatusSource { Player, Target }

public class StatusHelpers {
    private readonly Dictionary<StatusSource, Dictionary<uint, List<DalamudStatus>>> _map = new() {
        [StatusSource.Player] = [],
        [StatusSource.Target] = [],
    };

    public List<DalamudStatus> GetStatusList(StatusSource source, uint statusId) {
        var dict = _map[source];
        return dict.TryGetValue(statusId, out var list) ? list : [];
    }

    public void GenerateStatusMap() {
        foreach (var d in _map.Values) d.Clear();

        if (Plugin.ObjectTable.LocalPlayer is IBattleChara player)
            Populate(StatusSource.Player, player);

        var target = Plugin.TargetManager.SoftTarget ?? Plugin.TargetManager.Target;
        if (target is IBattleChara targetChara)
            Populate(StatusSource.Target, targetChara);
    }

    private void Populate(StatusSource src, IBattleChara chara) {
        var dict = _map[src];
        foreach (var s in chara.StatusList) {
            if (!dict.TryGetValue(s.StatusId, out var list))
                dict[s.StatusId] = list = [];
            list.Add(s);
        }
    }
}
