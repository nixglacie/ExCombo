using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.JobGauge.Types;

namespace ExCombo.Helpers;

public record GaugeField(string Name, Func<float> Get);

internal static class JobGaugeRegistry {
    public static IReadOnlyList<GaugeField>? GetFields(string job)
        => _fields.TryGetValue(job, out var f) ? f : null;

    private static readonly Dictionary<string, List<GaugeField>> _fields = new() {
        ["WAR"] = [
            new("BeastGauge",              () => Plugin.JobGauges.Get<WARGauge>().BeastGauge),
        ],
        ["PLD"] = [
            new("OathGauge",               () => Plugin.JobGauges.Get<PLDGauge>().OathGauge),
        ],
        ["DRK"] = [
            new("Blood",                   () => Plugin.JobGauges.Get<DRKGauge>().Blood),
            new("DarksideTimeRemaining",   () => Plugin.JobGauges.Get<DRKGauge>().DarksideTimeRemaining),
            new("HasDarkArts",             () => Plugin.JobGauges.Get<DRKGauge>().HasDarkArts ? 1 : 0),
        ],
        ["GNB"] = [
            new("Ammo",                    () => Plugin.JobGauges.Get<GNBGauge>().Ammo),
            new("AmmoComboStep",           () => Plugin.JobGauges.Get<GNBGauge>().AmmoComboStep),
        ],
        ["WHM"] = [
            new("Lily",                    () => Plugin.JobGauges.Get<WHMGauge>().Lily),
            new("BloodLily",               () => Plugin.JobGauges.Get<WHMGauge>().BloodLily),
            new("LilyTimer",               () => Plugin.JobGauges.Get<WHMGauge>().LilyTimer),
        ],
        ["SCH"] = [
            new("Aetherflow",              () => Plugin.JobGauges.Get<SCHGauge>().Aetherflow),
            new("FairyGauge",              () => Plugin.JobGauges.Get<SCHGauge>().FairyGauge),
            new("SeraphTimer",             () => Plugin.JobGauges.Get<SCHGauge>().SeraphTimer),
        ],
        ["AST"] = [
            new("DrawnCrownCard",          () => (float)Plugin.JobGauges.Get<ASTGauge>().DrawnCrownCard),
        ],
        ["SGE"] = [
            new("Addersgall",              () => Plugin.JobGauges.Get<SGEGauge>().Addersgall),
            new("Addersting",              () => Plugin.JobGauges.Get<SGEGauge>().Addersting),
        ],
        ["MNK"] = [
            new("Chakra",                  () => Plugin.JobGauges.Get<MNKGauge>().Chakra),
            new("OpoOpoFury",              () => Plugin.JobGauges.Get<MNKGauge>().OpoOpoFury),
            new("RaptorFury",              () => Plugin.JobGauges.Get<MNKGauge>().RaptorFury),
            new("CoeurlFury",              () => Plugin.JobGauges.Get<MNKGauge>().CoeurlFury),
            new("BlitzTimeRemaining",      () => Plugin.JobGauges.Get<MNKGauge>().BlitzTimeRemaining),
        ],
        ["DRG"] = [
            new("IsLOTDActive",            () => Plugin.JobGauges.Get<DRGGauge>().IsLOTDActive ? 1 : 0),
            new("LOTDTimer",               () => Plugin.JobGauges.Get<DRGGauge>().LOTDTimer),
            new("FirstmindsFocusCount",    () => Plugin.JobGauges.Get<DRGGauge>().FirstmindsFocusCount),
        ],
        ["NIN"] = [
            new("Ninki",                   () => Plugin.JobGauges.Get<NINGauge>().Ninki),
            new("Kazematoi",               () => Plugin.JobGauges.Get<NINGauge>().Kazematoi),
        ],
        ["SAM"] = [
            new("Kenki",                   () => Plugin.JobGauges.Get<SAMGauge>().Kenki),
            new("MeditationStacks",        () => Plugin.JobGauges.Get<SAMGauge>().MeditationStacks),
            new("HasGetsu",                () => Plugin.JobGauges.Get<SAMGauge>().HasGetsu ? 1 : 0),
            new("HasSetsu",                () => Plugin.JobGauges.Get<SAMGauge>().HasSetsu ? 1 : 0),
            new("HasKa",                   () => Plugin.JobGauges.Get<SAMGauge>().HasKa ? 1 : 0),
        ],
        ["RPR"] = [
            new("Soul",                    () => Plugin.JobGauges.Get<RPRGauge>().Soul),
            new("LemureShroud",            () => Plugin.JobGauges.Get<RPRGauge>().LemureShroud),
            new("VoidShroud",              () => Plugin.JobGauges.Get<RPRGauge>().VoidShroud),
        ],
        ["VPR"] = [
            new("RattlingCoilStacks",      () => Plugin.JobGauges.Get<VPRGauge>().RattlingCoilStacks),
            new("SerpentOffering",         () => Plugin.JobGauges.Get<VPRGauge>().SerpentOffering),
        ],
        ["BRD"] = [
            new("SoulVoice",               () => Plugin.JobGauges.Get<BRDGauge>().SoulVoice),
            new("Repertoire",              () => Plugin.JobGauges.Get<BRDGauge>().Repertoire),
            new("SongTimer",               () => Plugin.JobGauges.Get<BRDGauge>().SongTimer),
            new("Song",                    () => (float)Plugin.JobGauges.Get<BRDGauge>().Song),
        ],
        ["MCH"] = [
            new("Heat",                    () => Plugin.JobGauges.Get<MCHGauge>().Heat),
            new("Battery",                 () => Plugin.JobGauges.Get<MCHGauge>().Battery),
            new("IsOverheated",            () => Plugin.JobGauges.Get<MCHGauge>().IsOverheated ? 1 : 0),
            new("IsRobotActive",           () => Plugin.JobGauges.Get<MCHGauge>().IsRobotActive ? 1 : 0),
        ],
        ["DNC"] = [
            new("Feathers",                () => Plugin.JobGauges.Get<DNCGauge>().Feathers),
            new("Esprit",                  () => Plugin.JobGauges.Get<DNCGauge>().Esprit),
            new("IsDancing",               () => Plugin.JobGauges.Get<DNCGauge>().IsDancing ? 1 : 0),
            new("CompletedSteps",          () => Plugin.JobGauges.Get<DNCGauge>().CompletedSteps),
        ],
        ["BLM"] = [
            new("AstralFireStacks",        () => Plugin.JobGauges.Get<BLMGauge>().AstralFireStacks),
            new("UmbralIceStacks",         () => Plugin.JobGauges.Get<BLMGauge>().UmbralIceStacks),
            new("UmbralHearts",            () => Plugin.JobGauges.Get<BLMGauge>().UmbralHearts),
            new("PolyglotStacks",          () => Plugin.JobGauges.Get<BLMGauge>().PolyglotStacks),
            new("AstralSoulStacks",        () => Plugin.JobGauges.Get<BLMGauge>().AstralSoulStacks),
            new("EnochianTimer",           () => Plugin.JobGauges.Get<BLMGauge>().EnochianTimer),
            new("IsParadoxActive",         () => Plugin.JobGauges.Get<BLMGauge>().IsParadoxActive ? 1 : 0),
            new("InAstralFire",            () => Plugin.JobGauges.Get<BLMGauge>().InAstralFire ? 1 : 0),
            new("InUmbralIce",             () => Plugin.JobGauges.Get<BLMGauge>().InUmbralIce ? 1 : 0),
        ],
        ["SMN"] = [
            new("AttunementCount",         () => Plugin.JobGauges.Get<SMNGauge>().AttunementCount),
            new("SummonTimerRemaining",    () => Plugin.JobGauges.Get<SMNGauge>().SummonTimerRemaining),
            new("AttunementTimerRemaining",() => Plugin.JobGauges.Get<SMNGauge>().AttunementTimerRemaining),
            new("HasAetherflowStacks",     () => Plugin.JobGauges.Get<SMNGauge>().HasAetherflowStacks ? 1 : 0),
        ],
        ["RDM"] = [
            new("BlackMana",               () => Plugin.JobGauges.Get<RDMGauge>().BlackMana),
            new("WhiteMana",               () => Plugin.JobGauges.Get<RDMGauge>().WhiteMana),
            new("ManaStacks",              () => Plugin.JobGauges.Get<RDMGauge>().ManaStacks),
        ],
        ["PCT"] = [
            new("Paint",                   () => Plugin.JobGauges.Get<PCTGauge>().Paint),
            new("PalleteGauge",            () => Plugin.JobGauges.Get<PCTGauge>().PalleteGauge),
            new("CreatureMotifDrawn",      () => Plugin.JobGauges.Get<PCTGauge>().CreatureMotifDrawn ? 1 : 0),
            new("WeaponMotifDrawn",        () => Plugin.JobGauges.Get<PCTGauge>().WeaponMotifDrawn ? 1 : 0),
            new("LandscapeMotifDrawn",     () => Plugin.JobGauges.Get<PCTGauge>().LandscapeMotifDrawn ? 1 : 0),
            new("MooglePortraitReady",     () => Plugin.JobGauges.Get<PCTGauge>().MooglePortraitReady ? 1 : 0),
            new("MadeenPortraitReady",     () => Plugin.JobGauges.Get<PCTGauge>().MadeenPortraitReady ? 1 : 0),
        ],
    };
}
