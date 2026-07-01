using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;

namespace ExCombo.Helpers;

// Party-member queries (from Wrath Functions/Party.cs). Cached ~2s like Wrath.
internal static class PartyHelper {
    private static readonly List<IBattleChara> _cache = new();
    private static long _cacheTick;

    public static IReadOnlyList<IBattleChara> Members(bool allowCache = true) {
        var now = Environment.TickCount64;
        if (allowCache && _cache.Count > 0 && now - _cacheTick < 2000) return _cache;

        _cache.Clear();
        _cacheTick = now;
        var party = Plugin.PartyList;
        if (party.Length == 0) {
            // Solo: just the local player.
            if (Plugin.ObjectTable.LocalPlayer is { } me) _cache.Add(me);
            return _cache;
        }
        foreach (var m in party)
            if (m.GameObject is IBattleChara bc) _cache.Add(bc);
        return _cache;
    }

    public static float AvgHpPercent() {
        float sum = 0f; int n = 0;
        foreach (var m in Members()) {
            if (m.MaxHp == 0) continue;
            sum += m.CurrentHp * 100f / m.MaxHp; n++;
        }
        return n == 0 ? 0f : sum / n;
    }

    // Percentage of party members carrying the given status.
    public static float BuffPercent(uint statusId) {
        int has = 0, n = 0;
        foreach (var m in Members()) {
            n++;
            foreach (var s in m.StatusList) if (s.StatusId == statusId) { has++; break; }
        }
        return n == 0 ? 0f : has * 100f / n;
    }

    public static int MembersWithBuff(uint statusId) {
        int has = 0;
        foreach (var m in Members())
            foreach (var s in m.StatusList) if (s.StatusId == statusId) { has++; break; }
        return has;
    }

    public static IBattleChara? LowestHp() {
        IBattleChara? best = null; float bestPct = float.MaxValue;
        foreach (var m in Members()) {
            if (m.MaxHp == 0 || m.CurrentHp == 0) continue;
            var pct = m.CurrentHp * 100f / m.MaxHp;
            if (pct < bestPct) { bestPct = pct; best = m; }
        }
        return best;
    }

    public static int DeadCount() {
        int n = 0;
        foreach (var m in Members()) if (m.CurrentHp == 0) n++;
        return n;
    }

    public static IBattleChara? FirstDead() {
        foreach (var m in Members()) if (m.CurrentHp == 0) return m;
        return null;
    }
}
