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
        return Plugin.Actions.GetAdjustedActionId(baseId);
    }

    public uint? Resolve(uint adjustedActionId) {
        uint lastComboMove = Plugin.Actions.GetLastUsedActionId();

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
                        { var t = Environment.TickCount64; NodeTrace[group.Id] = (t, NodeStreak(group.Id, t), null); }
                    }

                    // Per-group mid-combo: find lastComboMove within this group and advance from it.
                    // Each group is checked independently so higher-priority groups (OGCDs) still
                    // get their fresh evaluation even when mid-sequence in a lower-priority group.
                    if (lastComboMove != 0) {
                        var (adv, found, hasOutgoing) = FindAndAdvance(flow, group.Id, lastComboMove, new HashSet<string>());
                        if (found) {
                            if (adv.HasValue) return adv;
                            if (hasOutgoing) continue; // next step blocked in this group — skip it
                            // terminal action: fall through to fresh WalkFromNode (restart this group)
                        }
                    }

                    var r = WalkFromNode(flow, group.Id, new HashSet<string>());
                    if (r.HasValue) return r;
                }
                continue;
            }

            // No groups: same two-phase logic from trigger
            if (lastComboMove != 0) {
                var (adv, found, hasOutgoing) = FindAndAdvance(flow, trigger.Id, lastComboMove, new HashSet<string>());
                if (found) {
                    if (adv.HasValue) return adv;
                    if (hasOutgoing) continue; // blocked
                    // terminal: fall through to restart
                }
            }

            var result = WalkFromNode(flow, trigger.Id, new HashSet<string>());
            if (result.HasValue) return result;
        }
        return null;
    }

    // Searches the subtree rooted at nodeId for an Action node matching targetActionId,
    // following condition branches as evaluated right now. When found, advances to the
    // next step via WalkFromNode and returns (nextAction, found=true, hasOutgoing).
    private (uint? next, bool found, bool hasOutgoing) FindAndAdvance(
        ComboFlow flow, string nodeId, uint targetActionId, HashSet<string> visited)
    {
        if (!visited.Add(nodeId)) return (null, false, false);

        FlowNode? node = null;
        foreach (var n in flow.Nodes) {
            if (n.Id == nodeId) { node = n; break; }
        }
        if (node == null) return (null, false, false);

        if (node.Type == NodeType.Condition) {
            bool eval = ConditionEvaluator.Eval(node);
            if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), eval); }
            foreach (var edge in flow.Edges) {
                if (edge.FromNodeId != nodeId) continue;
                if (edge.Branch != null && edge.Branch != eval) continue;
                if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
                var r = FindAndAdvance(flow, edge.ToNodeId, targetActionId, new HashSet<string>(visited));
                if (r.found) return r;
            }
            return (null, false, false);
        }

        if (node.Type == NodeType.Action) {
            if (node.ActionId == targetActionId) {
                if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), true); }
                bool hasOutgoing = false;
                uint? adv = null;
                foreach (var edge in flow.Edges) {
                    if (edge.FromNodeId != nodeId) continue;
                    hasOutgoing = true;
                    adv = WalkFromNode(flow, edge.ToNodeId, new HashSet<string> { nodeId });
                    if (adv.HasValue) break;
                }
                return (adv, true, hasOutgoing);
            }
            // Not the target — keep searching this action's children
            foreach (var edge in flow.Edges) {
                if (edge.FromNodeId != nodeId) continue;
                var r = FindAndAdvance(flow, edge.ToNodeId, targetActionId, new HashSet<string>(visited));
                if (r.found) return r;
            }
            return (null, false, false);
        }

        // Trigger / Group
        if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), null); }
        foreach (var edge in flow.Edges) {
            if (edge.FromNodeId != nodeId) continue;
            if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
            var r = FindAndAdvance(flow, edge.ToNodeId, targetActionId, new HashSet<string>(visited));
            if (r.found) return r;
        }
        return (null, false, false);
    }

    // Finds the first reachable Action from nodeId, following condition branches as evaluated.
    // Action nodes return themselves immediately — no recursion into their children.
    // Blocked condition branches return null without walking back to a previous action.
    private uint? WalkFromNode(ComboFlow flow, string nodeId, HashSet<string> visited) {
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
                var r = WalkFromNode(flow, edge.ToNodeId, new HashSet<string>(visited));
                if (r.HasValue) return r;
            }
            return null; // condition blocked — no walk-back
        }

        if (node.Type == NodeType.Action) {
            if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), true); }
            return node.ActionId; // first action found — return immediately, don't recurse
        }

        // Trigger / Group
        if (TraceEnabled) { var t = Environment.TickCount64; NodeTrace[nodeId] = (t, NodeStreak(nodeId, t), null); }
        foreach (var edge in flow.Edges) {
            if (edge.FromNodeId != nodeId) continue;
            if (TraceEnabled) { var t = Environment.TickCount64; EdgeTrace[edge.Id] = (t, EdgeStreak(edge.Id, t)); }
            var r = WalkFromNode(flow, edge.ToNodeId, new HashSet<string>(visited));
            if (r.HasValue) return r;
        }
        return null;
    }
}
