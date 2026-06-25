using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class DragoonDataSource : JobDataSource {
    private static readonly string[] Fields = ["LotD_Timer", "First_Broods_Gaze_Stacks", "Firstminds_Focus_Stacks"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private float _lotdTimer;
    private byte  _eyeCount;
    private byte  _firstmindsFocus;

    public override void Update() {
        var g = Plugin.JobGauges.Get<DRGGauge>();
        _lotdTimer       = MathF.Max(0f, g.LOTDTimer / 1000f);
        _eyeCount        = g.EyeCount;
        _firstmindsFocus = g.FirstmindsFocusCount;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _lotdTimer,
        1 => _eyeCount,
        2 => _firstmindsFocus,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 20f,
        1 => 2f,
        2 => 2f,
        _ => 0f,
    };
}
