using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class GunbreakerDataSource : JobDataSource {
    private static readonly string[] Fields = ["Cartridges"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _cartridges;

    public override void Update() {
        var g = Plugin.JobGauges.Get<GNBGauge>();
        _cartridges = g.Ammo;
    }

    public override float GetConditionValue(int i) => i switch { 0 => _cartridges, _ => 0f };
    public override float GetMaxValue(int i)       => i switch { 0 => 3f,          _ => 0f };
}
