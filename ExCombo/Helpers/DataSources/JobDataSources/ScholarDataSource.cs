using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class ScholarDataSource : JobDataSource {
    private static readonly string[] Fields = ["Aetherflow_Stacks", "Fairie", "Seraph_Timer"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte  _aetherflow;
    private byte  _fairyGauge;
    private float _seraphTimer;

    public override void Update() {
        var g = Plugin.JobGauges.Get<SCHGauge>();
        _aetherflow  = g.Aetherflow;
        _fairyGauge  = g.FairyGauge;
        _seraphTimer = MathF.Max(0f, g.SeraphTimer / 1000f);
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _aetherflow,
        1 => _fairyGauge,
        2 => _seraphTimer,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 3f,
        1 => 100f,
        2 => 22f,
        _ => 0f,
    };
}
