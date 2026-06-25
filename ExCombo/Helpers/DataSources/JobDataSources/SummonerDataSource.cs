using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class SummonerDataSource : JobDataSource {
    private static readonly string[] Fields = [
        "Aetherflow_Stacks", "Summon_Timer", "Attunement_Timer", "Attunement_Stacks",
    ];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private int   _aetherflow;
    private float _summonTimer;
    private float _attunementTimer;
    private int   _attunementStacks;

    public override void Update() {
        var g = Plugin.JobGauges.Get<SMNGauge>();
        _aetherflow       = g.AetherflowStacks;
        _summonTimer      = MathF.Max(0f, g.SummonTimerRemaining     / 1000f);
        _attunementTimer  = MathF.Max(0f, g.AttunementTimerRemaining / 1000f);
        _attunementStacks = g.AttunementCount;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _aetherflow,
        1 => _summonTimer,
        2 => _attunementTimer,
        3 => _attunementStacks,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 2f,
        1 => 15f,
        2 => 30f,
        3 => 6f,
        _ => 0f,
    };
}
