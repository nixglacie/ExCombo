using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class RedMageDataSource : JobDataSource {
    private static readonly string[] Fields = ["White_Mana", "Black_Mana", "Mana_Stacks"];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private byte _whiteMana;
    private byte _blackMana;
    private byte _manaStacks;

    public override void Update() {
        var g = Plugin.JobGauges.Get<RDMGauge>();
        _whiteMana  = g.WhiteMana;
        _blackMana  = g.BlackMana;
        _manaStacks = g.ManaStacks;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _whiteMana,
        1 => _blackMana,
        2 => _manaStacks,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 100f,
        1 => 100f,
        2 => 3f,
        _ => 0f,
    };
}
