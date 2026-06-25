using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class MonkDataSource : JobDataSource {
    private static readonly string[] Fields = [
        "Chakra_Stacks", "Blitz_Timer",
        "Opo_Fury", "Raptor_Fury", "Coeurl_Fury",
    ];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private int   _chakra;
    private float _blitzTimer;
    private int   _opoFury;
    private int   _raptorFury;
    private int   _coeurlFury;

    public override void Update() {
        var g = Plugin.JobGauges.Get<MNKGauge>();
        _chakra     = g.Chakra;
        _blitzTimer = MathF.Max(0f, g.BlitzTimeRemaining / 1000f);
        _opoFury    = g.OpoOpoFury;
        _raptorFury = g.RaptorFury;
        _coeurlFury = g.CoeurlFury;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _chakra,
        1 => _blitzTimer,
        2 => _opoFury,
        3 => _raptorFury,
        4 => _coeurlFury,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 5f,
        1 => 20f,
        2 => 2f,
        3 => 1f,
        4 => 2f,
        _ => 0f,
    };
}
