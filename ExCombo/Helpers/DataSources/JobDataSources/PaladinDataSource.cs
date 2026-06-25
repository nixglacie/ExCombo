using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class PaladinDataSource : JobDataSource {
    private static readonly string[] Fields = ["Oath"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _oath;

    public override void Update() {
        var g = Plugin.JobGauges.Get<PLDGauge>();
        _oath = g.OathGauge;
    }

    public override float GetConditionValue(int i) => i switch { 0 => _oath, _ => 0f };
    public override float GetMaxValue(int i)       => i switch { 0 => 100f,  _ => 0f };
}
