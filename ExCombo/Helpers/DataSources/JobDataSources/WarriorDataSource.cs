using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class WarriorDataSource : JobDataSource {
    private static readonly string[] Fields = ["Wrath"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _wrath;

    public override void Update() {
        var g = Plugin.JobGauges.Get<WARGauge>();
        _wrath = g.BeastGauge;
    }

    public override float GetConditionValue(int i) => i switch { 0 => _wrath, _ => 0f };
    public override float GetMaxValue(int i)       => i switch { 0 => 100f,   _ => 0f };
}
