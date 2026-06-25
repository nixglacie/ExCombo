using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class SageDataSource : JobDataSource {
    private static readonly string[] Fields = ["Addersgall_Timer", "Addersgall_Stacks", "Addersting_Stacks"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private float _addersgallTimer;
    private byte  _addersgall;
    private byte  _addersting;

    public override void Update() {
        var g = Plugin.JobGauges.Get<SGEGauge>();
        _addersgallTimer = MathF.Max(0f, g.AddersgallTimer / 1000f);
        _addersgall      = g.Addersgall;
        _addersting      = g.Addersting;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _addersgallTimer,
        1 => _addersgall,
        2 => _addersting,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 20f,
        1 => 3f,
        2 => 3f,
        _ => 0f,
    };
}
