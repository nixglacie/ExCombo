using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class DarkKnightDataSource : JobDataSource {
    private static readonly string[] Fields = ["Blood", "Darkside_Timer", "Shadow_Timer"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte  _blood;
    private float _darksideTimer;
    private float _shadowTimer;

    public override void Update() {
        var g = Plugin.JobGauges.Get<DRKGauge>();
        _blood         = g.Blood;
        _darksideTimer = MathF.Max(0f, g.DarksideTimeRemaining / 1000f);
        _shadowTimer   = MathF.Max(0f, g.ShadowTimeRemaining   / 1000f);
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _blood,
        1 => _darksideTimer,
        2 => _shadowTimer,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 60f,
        2 => 20f,
        _ => 0f,
    };
}
