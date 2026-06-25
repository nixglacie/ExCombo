using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class BardDataSource : JobDataSource {
    private static readonly string[] Fields = ["Song_Timer", "Repertoire_Stacks", "Soul_Voice"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private float _songTimer;
    private byte  _repertoire;
    private byte  _soulVoice;

    public override void Update() {
        var g = Plugin.JobGauges.Get<BRDGauge>();
        _songTimer  = MathF.Max(0f, g.SongTimer / 1000f);
        _repertoire = g.Repertoire;
        _soulVoice  = g.SoulVoice;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _songTimer,
        1 => _repertoire,
        2 => _soulVoice,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 45f,
        1 => 4f,
        2 => 100f,
        _ => 0f,
    };
}
