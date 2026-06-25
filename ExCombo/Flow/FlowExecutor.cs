using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ExCombo.Helpers;

namespace ExCombo.Flow;

public class FlowExecutor {
    internal static bool TraceEnabled;
    internal static readonly ConcurrentDictionary<string, (long Tick, long StreakStart, bool? Eval)> NodeTrace = new();
    internal static readonly ConcurrentDictionary<string, (long Tick, long StreakStart)>              EdgeTrace = new();

    private readonly Configuration _config;

    public FlowExecutor(Configuration config) {
        _config = config;
    }

    private static long NodeStreak(string id, long now) =>
        NodeTrace.TryGetValue(id, out var p) && now - p.Tick < 100 ? p.StreakStart : now;
    private static long EdgeStreak(string id, long now) =>
        EdgeTrace.TryGetValue(id, out var p) && now - p.Tick < 100 ? p.StreakStart : now;

    public uint? PeekNext() {
        var job = CharacterState.GetCharacterJob();
        if (!ActionHelpers.JobActionIDs.TryGetValue(job, out uint baseId)) return null;
        // GetAdjustedActionId fires the hook, which calls Resolve() internally.
        // Return that result directly — don't call Resolve() again with an already-resolved ID.
        return Plugin.Actions.GetAdjustedActionId(baseId);
    }

    public uint? Resolve(uint adjustedActionId) {
        foreach (var flow in _config.Flows) {
            if (!flow.Enabled) continue;

            FlowNode? trigger = null;
            foreach (var node in flow.Nodes) {
                if (node.Type == NodeType.Trigger && node.ActionId == adjustedActionId) {
                    trigger = node; break;
                }
            }
            if (trigger == null) continue;

            if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[trigger.Id] = (t, NodeStreak(trigger.Id, t), null); }

            var groups = new List<FlowNode>();
            foreach (var edge in flow.Edges) {
                if (edge.FromNodeId != trigger.Id) continue;
                foreach (var node in flow.Nodes) {
                    if (node.Id == edge.ToNodeId && node.Type == NodeType.Group) { groups.Add(node); break; }
                }
            }

            if (groups.Count > 0) {
                groups.Sort((a, b) => a.Priority.CompareTo(b.Priority));
                foreach (var group in groups) {
                    if (TraceEnabled) {
                        foreach (var e in flow.Edges)
                            if (e.FromNodeId == trigger.Id && e.ToNodeId == group.Id) {
                                { var t = Environment.TickCount64; EdgeTrace[e.Id] = (t, EdgeStreak(e.Id, t)); } break;
                            }
                    }
                    var r = Walk(flow, group.Id, new HashSet<string>());
                    if (r.HasValue) return r;
                }
                continue;
            }

            var result = Walk(flow, trigger.Id, new HashSet<string>());
            if (result.HasValue) return result;
        }
        return null;
    }

    private uint? Walk(ComboFlow flow, string nodeId, HashSet<string> visited) {
        if (!visited.Add(nodeId)) return null;

        FlowNode? node = null;
        foreach (var n in flow.Nodes) {
            if (n.Id == nodeId) { node = n; break; }
        }
        if (node == null) return null;

        if (node.Type == NodeType.Condition) {
            bool result = ConditionEvaluator.Eval(node);
            if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), result); }
            foreach (var edge in flow.Edges) {
                if (edge.FromNodeId != nodeId) continue;
                if (edge.Branch != null && edge.Branch != result) continue;
                if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
                var r = Walk(flow, edge.ToNodeId, new HashSet<string>(visited));
                if (r.HasValue) return r;
            }
            return null;
        }

        if (node.Type == NodeType.Action) {
            if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), true); }
            foreach (var edge in flow.Edges) {
                if (edge.FromNodeId == nodeId) {
                    if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
                    var r = Walk(flow, edge.ToNodeId, new HashSet<string>(visited));
                    if (r.HasValue) return r;
                }
            }
            return node.ActionId;
        }

        if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), null); }
        foreach (var edge in flow.Edges) {
            if (edge.FromNodeId == nodeId) {
                if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
                var r = Walk(flow, edge.ToNodeId, new HashSet<string>(visited));
                if (r.HasValue) return r;
            }
        }
        return null;
    }
}
