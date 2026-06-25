using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class ReaperDataSource : JobDataSource {
    private static readonly string[] Fields = [
        "Soul", "Shroud", "Enshroud_Timer", "Lemure_Shroud_Stacks", "Void_Shroud_Stacks",
    ];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte  _soul;
    private byte  _shroud;
    private float _enshroudTimer;
    private byte  _lemureShroud;
    private byte  _voidShroud;

    public override void Update() {
        var g = Plugin.JobGauges.Get<RPRGauge>();
        _soul          = g.Soul;
        _shroud        = g.Shroud;
        _enshroudTimer = MathF.Max(0f, g.EnshroudedTimeRemaining / 1000f);
        _lemureShroud  = g.LemureShroud;
        _voidShroud    = g.VoidShroud;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _soul,
        1 => _shroud,
        2 => _enshroudTimer,
        3 => _lemureShroud,
        4 => _voidShroud,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 100f,
        2 => 30f,
        3 => 5f,
        4 => 5f,
        _ => 0f,
    };
}
