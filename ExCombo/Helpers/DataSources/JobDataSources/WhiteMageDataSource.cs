using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class WhiteMageDataSource : JobDataSource {
    private static readonly string[] Fields = ["Lily_Timer", "Lily_Stacks", "Blood_Lily_Stacks"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private float _lilyTimer;
    private byte  _lily;
    private byte  _bloodLily;

    public override void Update() {
        var g = Plugin.JobGauges.Get<WHMGauge>();
        _lilyTimer = MathF.Max(0f, g.LilyTimer / 1000f);
        _lily      = g.Lily;
        _bloodLily = g.BloodLily;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _lilyTimer,
        1 => _lily,
        2 => _bloodLily,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 20f,
        1 => 3f,
        2 => 3f,
        _ => 0f,
    };
}
