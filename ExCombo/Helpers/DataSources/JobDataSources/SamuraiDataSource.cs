using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class SamuraiDataSource : JobDataSource {
    private static readonly string[] Fields = ["Kenki", "Meditation_Stacks"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _kenki;
    private byte _meditationStacks;

    public override void Update() {
        var g = Plugin.JobGauges.Get<SAMGauge>();
        _kenki            = g.Kenki;
        _meditationStacks = g.MeditationStacks;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _kenki,
        1 => _meditationStacks,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 5f,
        _ => 0f,
    };
}
