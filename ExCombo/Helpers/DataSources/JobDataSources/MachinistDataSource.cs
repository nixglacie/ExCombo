using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class MachinistDataSource : JobDataSource {
    private static readonly string[] Fields = ["Heat", "Overheat_Timer", "Battery", "Summon_Timer"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte  _heat;
    private float _overheatTimer;
    private byte  _battery;
    private float _summonTimer;

    public override void Update() {
        var g = Plugin.JobGauges.Get<MCHGauge>();
        _heat          = g.Heat;
        _overheatTimer = MathF.Max(0f, g.OverheatTimeRemaining / 1000f);
        _battery       = g.Battery;
        _summonTimer   = MathF.Max(0f, g.SummonTimeRemaining   / 1000f);
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _heat,
        1 => _overheatTimer,
        2 => _battery,
        3 => _summonTimer,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 10f,
        2 => 100f,
        3 => 12f,
        _ => 0f,
    };
}
