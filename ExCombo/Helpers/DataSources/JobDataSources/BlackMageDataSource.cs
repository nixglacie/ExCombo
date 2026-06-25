using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers.DataSources.JobDataSources;

public class BlackMageDataSource : JobDataSource {
    private static readonly string[] Fields = [
        "Enochian_Timer", "Polyglot_Stacks",
        "Umbral_Ice_Stacks", "Astral_Fire_Stacks",
        "Umbral_Hearts", "Astral_Soul_Stacks",
    ];
    public override IReadOnlyList<string> ConditionFieldNames => Fields;

    private float _enochianTimer;
    private int   _polyglotStacks;
    private int   _umbralIceStacks;
    private int   _astralFireStacks;
    private int   _umbralHearts;
    private int   _astralSoulStacks;

    public override void Update() {
        var g = Plugin.JobGauges.Get<BLMGauge>();
        _enochianTimer    = MathF.Max(0f, g.EnochianTimer / 1000f);
        _polyglotStacks   = g.PolyglotStacks;
        _umbralIceStacks  = g.UmbralIceStacks;
        _astralFireStacks = g.AstralFireStacks;
        _umbralHearts     = g.UmbralHearts;
        _astralSoulStacks = g.AstralSoulStacks;
    }

    public override float GetConditionValue(int i) => i switch {
        0 => _enochianTimer,
        1 => _polyglotStacks,
        2 => _umbralIceStacks,
        3 => _astralFireStacks,
        4 => _umbralHearts,
        5 => _astralSoulStacks,
        _ => 0f,
    };

    public override float GetMaxValue(int i) => i switch {
        0 => 30f,
        1 => 3f,
        2 => 3f,
        3 => 3f,
        4 => 3f,
        5 => 6f,
        _ => 0f,
    };
}
