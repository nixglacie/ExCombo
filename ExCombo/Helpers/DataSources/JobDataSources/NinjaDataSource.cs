using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class NinjaDataSource : JobDataSource {
    private static readonly string[] Fields = ["Kazematoi", "Ninki"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _kazematoi;
    private byte _ninki;

    public override void Update() {
        var g = Plugin.JobGauges.Get<NINGauge>();
        _kazematoi = g.Kazematoi;
        _ninki     = g.Ninki;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _kazematoi,
        1 => _ninki,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 5f,
        1 => 100f,
        _ => 0f,
    };
}
