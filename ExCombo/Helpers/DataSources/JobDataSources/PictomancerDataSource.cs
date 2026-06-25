using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class PictomancerDataSource : JobDataSource {
    private static readonly string[] Fields = ["Palette", "Paint"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _palette;
    private byte _paint;

    public override void Update() {
        var g = Plugin.JobGauges.Get<PCTGauge>();
        _palette = g.PalleteGauge;
        _paint   = g.Paint;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _palette,
        1 => _paint,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 5f,
        _ => 0f,
    };
}
