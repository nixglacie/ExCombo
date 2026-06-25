using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class ViperDataSource : JobDataSource {
    private static readonly string[] Fields = [
        "Rattling_Coil_Stacks", "Serpent_Offering", "Anguine_Tribute",
    ];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _rattlingCoil;
    private byte _serpentOffering;
    private byte _anguineTribute;

    public override void Update() {
        var g = Plugin.JobGauges.Get<VPRGauge>();
        _rattlingCoil    = g.RattlingCoilStacks;
        _serpentOffering = g.SerpentOffering;
        _anguineTribute  = g.AnguineTribute;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _rattlingCoil,
        1 => _serpentOffering,
        2 => _anguineTribute,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 3f,
        1 => 100f,
        2 => 5f,
        _ => 0f,
    };
}
