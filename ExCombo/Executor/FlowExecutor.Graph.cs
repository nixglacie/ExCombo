using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using ExCombo.Flow;
using ExCombo.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ExCombo;

internal static partial class FlowExecutor {
    // ── Graph walker ─────────────────────────────────────────────────────────

    // Forward flow edge from a node's output port. ToPortIndex >= 1 edges are Logic-node predicate
    // wires and never carry flow, so every traversal lookup must go through this filter.
    private static FlowEdge? FlowEdgeFrom(ComboFlow f, string nodeId, int port) =>
        f.Edges.Find(e => e.FromNodeId == nodeId && e.FromPortIndex == port && e.ToPortIndex == 0);

    private static void AdvanceState(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        if (state.CurrentBranchId != null) {
            var routeNode = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (routeNode == null || !state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
                state.CurrentBranchId    = null;
                state.ActiveBranchNodeId = null;
                state.ActivePath.Clear();
                FindInitialAction(flow, trigger, state);
                return;
            }
            // Advance the active port's progress, then re-resolve from the top (priority order).
            // Lower ports keep their progress, so a preempted chain resumes where it left off.
            var port   = state.CurrentBranchPort;
            var chain  = GetActiveChain(flow, routeNode, port);
            var newIdx = bs.GetProgress(port) + 1;
            // Chain end flowing into a trailing Branch → park past the end (progress == count);
            // resolution then routes through the trailing branch instead of wrapping to the head.
            if (newIdx >= chain.Count && ChainTrailingBranch(flow, chain) != null)
                bs.SetProgress(port, chain.Count);
            else
                bs.SetProgress(port, newIdx < chain.Count ? newIdx : 0);
            // Re-resolve from the outermost entry node so higher-priority OUTER ports can preempt;
            // the (possibly nested) inner branch keeps its port progress and resumes if selected.
            var top = state.ActiveBranchNodeId != null ? flow.Nodes.Find(n => n.Id == state.ActiveBranchNodeId) : null;
            ReResolve(flow, state, top ?? routeNode);
        } else {
            var edge = FlowEdgeFrom(flow, state.NextActionId, 0);
            if (edge == null) { FindInitialAction(flow, trigger, state); return; }
            var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
            if      (next == null || next.Type == NodeType.Trigger) FindInitialAction(flow, trigger, state);
            else if (next.Type == NodeType.Action)                  state.NextActionId = next.Id;
            else if (next.Type == NodeType.Branch)                  ResolveBranch(flow, state, next);
            else if (FlowNode.IsGate(next.Type))                    EnterCondition(flow, state, next);
        }
    }

    private static void FindInitialAction(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        state.ActivePath.Clear();   // rebuilt below when routing lands in a branch
        var edge = FlowEdgeFrom(flow, trigger.Id, 0);
        if (edge == null) { state.NextActionId = ""; return; }
        var next = flow.Nodes.Find(n => n.Id == edge.ToNodeId);
        if      (next == null)                  state.NextActionId = "";
        else if (next.Type == NodeType.Action)  state.NextActionId = next.Id;
        else if (next.Type == NodeType.Branch)  ResolveBranch(flow, state, next);
        else if (FlowNode.IsGate(next.Type))    EnterCondition(flow, state, next);
        else                                    state.NextActionId = "";
    }

    // Re-evaluate a Branch or standalone Condition node from scratch.
    private static void ReResolve(ComboFlow flow, FlowRunState state, FlowNode node) {
        if      (node.Type == NodeType.Branch) ResolveBranch(flow, state, node);
        else if (FlowNode.IsGate(node.Type))   EnterCondition(flow, state, node);
    }

    // The action chain currently routed through a branch port (resolves nested Condition gates).
    private static List<FlowNode> GetActiveChain(ComboFlow flow, FlowNode routeNode, int port) {
        if (FlowNode.IsGate(routeNode.Type)) return GetPortChain(flow, routeNode.Id, port);
        var portEdge  = FlowEdgeFrom(flow, routeNode.Id, port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        return firstNode != null && FlowNode.IsGate(firstNode.Type)
            ? GetPortChain(flow, firstNode.Id, 0)   // condition gate → its true-port chain
            : GetPortChain(flow, routeNode.Id, port);
    }

    // Chain + current candidate for a branch port; null if disconnected or the condition gate is
    // closed. No usability/weave gating — callers decide that.
    private static (List<FlowNode> chain, int progress, FlowNode cand)? ResolvePort(
            ComboFlow flow, FlowNode branchNode, BranchNodeState bs, int port) {
        var portEdge  = FlowEdgeFrom(flow, branchNode.Id, port);
        var firstNode = portEdge != null ? flow.Nodes.Find(n => n.Id == portEdge.ToNodeId) : null;
        if (firstNode == null) return null;

        List<FlowNode> chain;
        if (FlowNode.IsGate(firstNode.Type)) {
            // Mid-chain: gate already passed at entry; let the started chain finish (e.g. the
            // Continuation that follows a Burst Strike which just spent the cartridge the gate reads).
            bool midChain = port == bs.ActivePort && bs.GetProgress(port) > 0;
            if (!midChain) {
                if (!EvaluateCondition(flow, firstNode)) return null;       // gate closed at entry
                bs.SetProgress(port, 0);                                    // fresh entry → chain head
            }
            chain = GetPortChain(flow, firstNode.Id, 0);
        } else {
            chain = GetPortChain(flow, branchNode.Id, port);
        }
        if (chain.Count == 0) return null;

        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) {
            // Parked past the chain end inside a trailing Branch: surface the branch as the
            // candidate — the caller recurses into it. Otherwise wrap to the chain head as before.
            if (progress == chain.Count && ChainTrailingBranch(flow, chain) is { } trail)
                return (chain, progress, trail);
            bs.SetProgress(port, 0); progress = 0;
        }
        return (chain, progress, chain[progress]);
    }

    // Strict priority selector: walk ports top→bottom, surface the first eligible one. Higher ports
    // preempt lower ones immediately — except a committed combo group holds the branch (only a
    // strictly-higher oGCD port may weave in) until it completes.
    private static void ResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        state.ActiveBranchNodeId = branchNode.Id;
        state.ActivePath.Clear();
        // Commit pointing at a node deleted in the editor → clear before resolving.
        if (state.CommittedBranchId != null && flow.Nodes.Find(n => n.Id == state.CommittedBranchId) == null) {
            state.CommittedBranchId = null;
            state.CommittedPort     = -1;
        }
        if (!TryResolveBranch(flow, state, branchNode, new HashSet<string>())) {
            // Nothing eligible this frame — fall back to the raw trigger; keep re-evaluating.
            state.CurrentBranchId = null;
            state.NextActionId    = "";
        }
    }

    // Recursive worker: a port routing into another Branch (directly or across one passed gate)
    // resolves that branch as a nested level — CurrentBranchId ends up at the INNERMOST branch
    // owning the surfaced chain, while ActiveBranchNodeId stays the outermost entry so per-frame
    // re-evaluation preserves outer-priority preemption. Returns false without touching run state
    // so the caller falls through to its next port. `seen` breaks Branch→Branch cycles and resolves
    // a diamond-shared inner branch once per pass.
    //
    // Group commitment is per-level: CommittedBranchId is the chain-owning (inner) branch, and a
    // higher-priority OUTER port may still preempt it — the group's progress persists and resumes
    // when the branch is selected again. Nested oGCD-led inner ports don't weave into an outer
    // committed hold (CommitActive/ResolvePort see nested ports as null).
    private static bool TryResolveBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode, HashSet<string> seen) {
        if (!seen.Add(branchNode.Id)) return false;   // cycle / already resolved this pass

        if (!state.BranchStates.TryGetValue(branchNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[branchNode.Id] = bs;
        }

        // A group port is "committed" once we've advanced PAST its first node (the previous chain
        // step shares the same group). Group entry stays priority-governed, even with a loose prefix.
        bool CommitActive(int port) {
            var r = ResolvePort(flow, branchNode, bs, port);
            if (r is not { } rr || rr.cand.GroupId == null || rr.progress == 0) return false;
            return rr.chain[rr.progress - 1].GroupId == rr.cand.GroupId;
        }
        if (state.CommittedBranchId == branchNode.Id && state.CommittedPort >= 0 && !CommitActive(state.CommittedPort)) {
            state.CommittedPort     = -1;
            state.CommittedBranchId = null;
        }
        if (state.CommittedPort < 0 && state.CurrentBranchId == branchNode.Id && CommitActive(state.CurrentBranchPort)) {
            state.CommittedPort     = state.CurrentBranchPort;
            state.CommittedBranchId = branchNode.Id;
        }

        // Committed group holds the branch — only a strictly-higher oGCD port may weave in.
        if (state.CommittedBranchId == branchNode.Id && state.CommittedPort >= 0) {
            for (var p = 0; p < state.CommittedPort; p++) {
                var rp = ResolvePort(flow, branchNode, bs, p);
                if (rp is not { } w || !ActionHelper.IsOgcd(w.cand.ActionId)) continue;
                if (!WeaveWindowOpen(state) || !IsUsableForWeave(w.cand.ActionId)) continue;
                bs.ActivePort           = p;
                state.CurrentBranchId   = branchNode.Id;
                state.CurrentBranchPort = p;
                state.NextActionId      = w.cand.Id;
                state.ActivePath.Add((branchNode.Id, p));
                return true;
            }
            var g = ResolvePort(flow, branchNode, bs, state.CommittedPort)!.Value;
            bs.ActivePort           = state.CommittedPort;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = state.CommittedPort;
            state.NextActionId      = g.cand.Id;
            state.ActivePath.Add((branchNode.Id, state.CommittedPort));
            return true;
        }

        for (var port = 0; port < branchNode.OutputCount; port++) {
            // Port routes into another Branch → recurse; the inner branch owns the surfaced chain.
            if (PortNestedBranch(flow, branchNode, port) is { } nested) {
                if (TryResolveBranch(flow, state, nested, seen)) {
                    bs.ActivePort = port;
                    state.ActivePath.Add((branchNode.Id, port));
                    NestedLog(state, $"[ExCombo][Branch] nested {branchNode.Id}:{port} → {nested.Id} surfaced {state.NextActionId}");
                    return true;
                }
                NestedLog(state, $"[ExCombo][Branch] nested {branchNode.Id}:{port} → {nested.Id} no eligible port, next port");
                continue;   // inner branch nothing eligible this frame — try the next port
            }

            var r = ResolvePort(flow, branchNode, bs, port);
            if (r is not { } rr) continue;

            // Chain end parked in a trailing Branch → recurse; the inner branch owns from here on
            // (until the run resets — priority nodes never "complete", so there is no way back to
            // the chain head short of a reset or a gated re-entry after preemption).
            if (rr.cand.Type == NodeType.Branch) {
                if (TryResolveBranch(flow, state, rr.cand, seen)) {
                    bs.ActivePort = port;
                    state.ActivePath.Add((branchNode.Id, port));
                    NestedLog(state, $"[ExCombo][Branch] trailing {branchNode.Id}:{port} → {rr.cand.Id} surfaced {state.NextActionId}");
                    return true;
                }
                NestedLog(state, $"[ExCombo][Branch] trailing {branchNode.Id}:{port} → {rr.cand.Id} no eligible port, next port");
                continue;
            }

            // oGCD ports are eligible only inside the weave window and when actually usable.
            if (ActionHelper.IsOgcd(rr.cand.ActionId)) {
                // A mid-chain oGCD continuation (e.g. Hypervelocity after Burst Strike) is enabled by
                // a buff the game applies a frame late. Within the grace window hold it instead of
                // letting a lower-priority port preempt it; otherwise honour usability as normal.
                bool midChain    = port == bs.ActivePort && rr.progress > 0;
                bool withinGrace = Environment.TickCount64 - state.LastPressedTick <= ComboGraceMs;
                bool offerable   = WeaveWindowOpen(state)
                                && (IsUsableForWeave(rr.cand.ActionId) || (midChain && withinGrace));
                if (!offerable) {
                    // A mid-chain inline oGCD we can't weave must not block the spine or surrender
                    // priority — skip it (and any consecutive oGCDs) and surface the next GCD in THIS
                    // chain, keeping the port active. Fresh oGCD-led ports still drop as before.
                    if (midChain) {
                        var i = rr.progress;
                        while (i < rr.chain.Count && ActionHelper.IsOgcd(rr.chain[i].ActionId)) i++;
                        if (i < rr.chain.Count) {
                            Plugin.LogDebug($"[ExCombo][Branch] skip oGCD={rr.cand.ActionId} → next={rr.chain[i].ActionId}");
                            bs.SetProgress(port, i);
                            bs.ActivePort           = port;
                            state.CurrentBranchId   = branchNode.Id;
                            state.CurrentBranchPort = port;
                            state.NextActionId      = rr.chain[i].Id;
                            state.ActivePath.Add((branchNode.Id, port));
                            return true;
                        }
                    }
                    continue;   // fresh oGCD-led port not ready, or no GCD left in chain → lower priority
                }
            }
            // GCD ports win by priority alone — the game queues until the GCD is ready.

            bs.ActivePort           = port;
            state.CurrentBranchId   = branchNode.Id;
            state.CurrentBranchPort = port;
            state.NextActionId      = rr.cand.Id;
            state.ActivePath.Add((branchNode.Id, port));
            return true;
        }

        return false;
    }

    // Nested-resolution debug lines fire every frame via ReResolve — only log on change.
    private static void NestedLog(FlowRunState state, string msg) {
        if (state.LastNestedLog == msg) return;
        state.LastNestedLog = msg;
        Plugin.LogDebug(msg);
    }

    // The inner Branch a branch port routes into — directly, or across ONE passed condition gate's
    // true port. Null when the port heads a plain action chain, is disconnected, or the gate is
    // closed (a closed gate then falls through to ResolvePort, which also rejects it). A Branch
    // after action nodes is handled separately as a trailing branch (see ChainTrailingBranch).
    private static FlowNode? PortNestedBranch(ComboFlow flow, FlowNode branchNode, int port) {
        var edge = FlowEdgeFrom(flow, branchNode.Id, port);
        var node = edge != null ? flow.Nodes.Find(n => n.Id == edge.ToNodeId) : null;
        if (node == null) return null;
        if (node.Type == NodeType.Branch) return node;
        if (!FlowNode.IsGate(node.Type) || !EvaluateCondition(flow, node)) return null;
        return PortTargetBranch(flow, node.Id, 0);
    }

    // The Branch node a port's flow edge points at directly, or null.
    private static FlowNode? PortTargetBranch(ComboFlow flow, string nodeId, int port) {
        var edge = FlowEdgeFrom(flow, nodeId, port);
        var tgt  = edge != null ? flow.Nodes.Find(n => n.Id == edge.ToNodeId) : null;
        return tgt is { Type: NodeType.Branch } ? tgt : null;
    }

    // The Branch node the chain's LAST action flows into, or null. A chain that continues into a
    // priority node hands control to it once its last action has fired (progress parks at
    // chain.Count instead of wrapping to the head).
    private static FlowNode? ChainTrailingBranch(ComboFlow flow, List<FlowNode> chain) =>
        chain.Count == 0 ? null : PortTargetBranch(flow, chain[^1].Id, 0);

    private static void EnterCondition(ComboFlow flow, FlowRunState state, FlowNode condNode) {
        state.ActiveBranchNodeId = condNode.Id;
        state.ActivePath.Clear();
        bool result = EvaluateCondition(flow, condNode);
        int  port   = result ? 0 : 1;

        // A gate port routing into a Branch resolves that branch as a nested level beneath the gate
        // (the gate stays the outermost re-entry node, so it re-evaluates every frame). The gate
        // already decided the route — an empty inner branch surfaces nothing rather than falling
        // back to the gate's other port.
        var chain = GetPortChain(flow, condNode.Id, port);
        if (chain.Count == 0) {
            if (PortTargetBranch(flow, condNode.Id, port) is { } b) { RouteConditionBranch(flow, state, b); return; }
            port ^= 1;
            chain = GetPortChain(flow, condNode.Id, port);
            if (chain.Count == 0 && PortTargetBranch(flow, condNode.Id, port) is { } b2) { RouteConditionBranch(flow, state, b2); return; }
        }
        if (chain.Count == 0) { state.NextActionId = ""; return; }

        if (!state.BranchStates.TryGetValue(condNode.Id, out var bs)) {
            bs = new BranchNodeState();
            state.BranchStates[condNode.Id] = bs;
        }
        if (bs.ActivePort >= 0 && bs.ActivePort != port) bs.SetProgress(bs.ActivePort, 0);
        var progress = bs.GetProgress(port);
        if (progress >= chain.Count) {
            // Parked past the chain end inside a trailing Branch → route through it.
            if (progress == chain.Count && ChainTrailingBranch(flow, chain) is { } tb) {
                RouteConditionBranch(flow, state, tb);
                return;
            }
            bs.SetProgress(port, 0); progress = 0;
        }
        bs.ActivePort           = port;
        state.CurrentBranchId   = condNode.Id;
        state.CurrentBranchPort = port;
        state.NextActionId      = chain[progress].Id;
    }

    private static void RouteConditionBranch(ComboFlow flow, FlowRunState state, FlowNode branchNode) {
        if (!TryResolveBranch(flow, state, branchNode, new HashSet<string>())) {
            state.CurrentBranchId = null;
            state.NextActionId    = "";   // nothing eligible this frame; re-evaluated next frame
        }
    }

    private static List<FlowNode> GetPortChain(ComboFlow flow, string branchNodeId, int portIndex) {
        var chain = new List<FlowNode>();
        var edge  = FlowEdgeFrom(flow, branchNodeId, portIndex);
        if (edge == null) return chain;

        var current = edge.ToNodeId;
        var visited = new HashSet<string>();
        while (!visited.Contains(current)) {
            visited.Add(current);
            var node = flow.Nodes.Find(n => n.Id == current);
            if (node is not { Type: NodeType.Action }) break;
            chain.Add(node);
            var next = FlowEdgeFrom(flow, current, 0);
            if (next == null) break;
            current = next.ToNodeId;
        }
        return chain;
    }

    // Reset only the active chain back to its first step, leaving branch/condition/weave state intact.
    private static void ResetActiveChain(ComboFlow flow, FlowNode trigger, FlowRunState state) {
        if (state.CurrentBranchId != null
            && state.BranchStates.TryGetValue(state.CurrentBranchId, out var bs)) {
            var node = flow.Nodes.Find(n => n.Id == state.CurrentBranchId);
            if (node != null) {
                // Break inside a combo group → restart at the group's first node, not the chain head.
                var chain   = GetActiveChain(flow, node, state.CurrentBranchPort);
                var prog    = bs.GetProgress(state.CurrentBranchPort);
                var resetTo = 0;
                if (prog > 0 && prog < chain.Count && chain[prog].GroupId is { } gid) {
                    resetTo = prog;
                    while (resetTo > 0 && chain[resetTo - 1].GroupId == gid) resetTo--;
                }
                bs.SetProgress(state.CurrentBranchPort, resetTo);
                var top = state.ActiveBranchNodeId != null ? flow.Nodes.Find(n => n.Id == state.ActiveBranchNodeId) : null;
                ReResolve(flow, state, top ?? node);
                return;
            }
        }
        FindInitialAction(flow, trigger, state);                  // linear chain → chain head
    }
}
