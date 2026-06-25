using System;
using System.Collections.Generic;
using ExCombo.Helpers.DataSources.JobDataSources;

namespace ExCombo.Helpers.DataSources;

public static class JobDataSourceRegistry {
    private static readonly Dictionary<Job, JobDataSource> _sources = new() {
        [Job.WAR] = new WarriorDataSource(),
        [Job.MRD] = new WarriorDataSource(),
        [Job.DRK] = new DarkKnightDataSource(),
        [Job.GNB] = new GunbreakerDataSource(),
        [Job.PLD] = new PaladinDataSource(),
        [Job.GLA] = new PaladinDataSource(),
        [Job.WHM] = new WhiteMageDataSource(),
        [Job.CNJ] = new WhiteMageDataSource(),
        [Job.SCH] = new ScholarDataSource(),
        [Job.SGE] = new SageDataSource(),
        [Job.BRD] = new BardDataSource(),
        [Job.ARC] = new BardDataSource(),
        [Job.MCH] = new MachinistDataSource(),
        [Job.DNC] = new DancerDataSource(),
        [Job.BLM] = new BlackMageDataSource(),
        [Job.THM] = new BlackMageDataSource(),
        [Job.SMN] = new SummonerDataSource(),
        [Job.ACN] = new SummonerDataSource(),
        [Job.RDM] = new RedMageDataSource(),
        [Job.PCT] = new PictomancerDataSource(),
        [Job.NIN] = new NinjaDataSource(),
        [Job.ROG] = new NinjaDataSource(),
        [Job.SAM] = new SamuraiDataSource(),
        [Job.DRG] = new DragoonDataSource(),
        [Job.LNC] = new DragoonDataSource(),
        [Job.MNK] = new MonkDataSource(),
        [Job.PGL] = new MonkDataSource(),
        [Job.RPR] = new ReaperDataSource(),
        [Job.VPR] = new ViperDataSource(),
    };

    public static JobDataSource? GetForJob(Job job) =>
        _sources.TryGetValue(job, out var ds) ? ds : null;

    public static JobDataSource? GetForJobString(string jobStr) =>
        Enum.TryParse<Job>(jobStr, true, out var job) ? GetForJob(job) : null;
}
