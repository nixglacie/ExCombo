using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class DancerDataSource : JobDataSource {
    private static readonly string[] Fields = ["Feather_Stacks", "Esprit", "Completed_Steps"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _feathers;
    private byte _esprit;
    private byte _completedSteps;

    public override void Update() {
        var g = Plugin.JobGauges.Get<DNCGauge>();
        _feathers       = g.Feathers;
        _esprit         = g.Esprit;
        _completedSteps = g.CompletedSteps;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _feathers,
        1 => _esprit,
        2 => _completedSteps,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 4f,
        1 => 100f,
        2 => 4f,
        _ => 0f,
    };
}
