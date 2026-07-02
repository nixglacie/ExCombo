using System;
using System.Collections.Generic;
using Dalamud.Interface;
using ExCombo.Flow;

namespace ExCombo.Helpers;

public sealed record WikiPort(string Label, string Description);

// One node-wiki page: everything the wiki window needs to draw and describe a node type.
// Template builds the mock FlowNode the preview renderer draws (sets OutputCount, badge flags, etc.).
public sealed record WikiEntry(
    NodeType        Type,
    string          Name,
    string          Category,
    FontAwesomeIcon Glyph,
    string          Summary,
    string[]        HowItWorks,
    WikiPort[]      Ports,
    string[]        Tips,
    Func<FlowNode>  Template);

// Static content for the Node Wiki window. Condition sub-check tables are NOT listed here — the
// wiki builds those live from ConditionCatalog so new checks show up automatically.
// Text may embed {ogcd}, {retarget} and {combo} as standalone words; NodeWikiWindow renders them
// as the matching inline status badge.
internal static class NodeWikiCatalog {
    internal const string CatCore      = "Core Flow";
    internal const string CatCondition = "Conditions";
    internal const string CatLogic     = "Logic";
    internal const string CatUtility   = "Utility";

    // Shared routing text for all condition gates.
    private static readonly string[] GateHow = {
        "Evaluates its check against live game state. When the result is true the flow continues " +
        "from output 1 (green, top); when false it continues from output 2 (red, bottom).",
        "The gate is re-checked every time the flow passes through it, so the taken side can change " +
        "from one press to the next as buffs fall off, cooldowns come back, and gauges fill.",
        "If the side the gate wants to take has nothing wired, the other side is used as a fallback " +
        "instead of dead-ending the chain.",
    };

    private static readonly WikiPort[] GatePorts = {
        new("In",            "Comes from the previous node in the chain."),
        new("Out 1 (green)", "Taken while the check is true."),
        new("Out 2 (red)",   "Taken while the check is false."),
    };

    internal static readonly IReadOnlyList<WikiEntry> Entries = new List<WikiEntry> {

        // ── Core Flow ────────────────────────────────────────────────────
        new(NodeType.Trigger, "Trigger", CatCore, FontAwesomeIcon.Bolt,
            "The entry point of a flow: pick a hotbar action and ExCombo takes over that button.",
            new[] {
                "While the flow is enabled, the chosen action's hotbar slot shows whatever step of " +
                "this chain is current, and pressing it fires that step instead of the original action.",
                "Each Trigger keeps its own run state, so one flow can drive several buttons " +
                "independently — every Trigger walks its own copy of the chain.",
                "The chain returns to the start after a period of inactivity (Chain reset, default " +
                "15 s — tunable globally and per flow) or when a game combo the chain models is broken.",
            },
            new[] {
                new WikiPort("Out", "The first node evaluated when the chain starts or resets."),
            },
            new[] {
                "A flow can contain multiple Triggers.",
                "Nothing can wire into a Trigger — it has no input port.",
            },
            () => new FlowNode { Type = NodeType.Trigger, ActionLabel = "Trigger" }),

        new(NodeType.Action, "Action", CatCore, FontAwesomeIcon.Magic,
            "One step in the chain: it shows on the trigger button, fires on press, then advances.",
            new[] {
                "While this is the current step, the trigger's hotbar slot shows this action. " +
                "Pressing the button casts it and moves the chain to the node wired to its output.",
                "oGCD actions {ogcd} are treated as weaves: they are only offered while the GCD " +
                "is rolling, the weave budget for this GCD isn't spent (Max weaves/GCD, default 2), and " +
                "the action is actually usable. Otherwise they are skipped automatically and the next " +
                "GCD in the chain shows instead — an oGCD never blocks your rotation.",
                "A retarget priority {retarget} redirects the cast to the first valid resolver " +
                "in your list — self, lowest-HP ally, mouseover, focus, party slots and more — without " +
                "changing your actual target.",
                "Actions grouped as a combo {combo} run to completion before an upstream Priority " +
                "node re-evaluates, so multi-step sequences aren't interrupted halfway.",
            },
            new[] {
                new WikiPort("In",  "Comes from the previous node in the chain."),
                new WikiPort("Out", "The next step after this action fires."),
            },
            new[] {
                "Double-click to change the action; the oGCD flag {ogcd} is detected automatically.",
                "Right-click → Retarget {retarget} to build the retarget priority chain.",
                "Select several actions and right-click → Group as Combo {combo} to make them atomic.",
            },
            () => new FlowNode {
                Type = NodeType.Action, ActionLabel = "Action",
                IsOgcd = true, GroupId = "wiki", RetargetPriority = new List<int> { 1 },
            }),

        new(NodeType.Branch, "Priority", CatCore, FontAwesomeIcon.CodeBranch,
            "A strict priority selector: the highest wired output whose chain is eligible wins, every frame.",
            new[] {
                "Outputs are checked top to bottom (1 = highest priority). The first eligible chain " +
                "surfaces on the trigger button, and a higher port becoming eligible preempts a lower " +
                "one instantly.",
                "Each port remembers its own progress, so a chain that was preempted resumes where it " +
                "left off when its turn comes back.",
                "Ports that lead with an oGCD {ogcd} are only eligible inside a weave window while " +
                "that oGCD is usable; GCD-led ports compete on priority alone.",
                "If a port's first node is a condition gate, the gate is checked at entry — a closed " +
                "gate skips that port entirely.",
                "A committed combo group {combo} holds the branch until the group completes; only a " +
                "strictly higher oGCD port may weave in during it.",
            },
            new[] {
                new WikiPort("In",       "Comes from the previous node in the chain."),
                new WikiPort("Out 1..N", "Priority slots, top to bottom; 1 is checked first."),
            },
            new[] {
                "Double-click to change the number of outputs.",
                "Put your highest-value / most conditional chain on port 1 and a fallback filler on the last port.",
            },
            () => new FlowNode { Type = NodeType.Branch, OutputCount = 3 }),

        // ── Conditions ───────────────────────────────────────────────────
        new(NodeType.GaugeCondition, "Gauge Condition", CatCondition, FontAwesomeIcon.ChartBar,
            "Compares a job-gauge value — cartridges, soul, chakra, feathers… — against a number.",
            Append(GateHow,
                "The available fields depend on the flow's job (the node shows the job's icon). " +
                "Boolean-style fields (Has…/Is…/In…) use a simple true/false check instead of a compare.",
                "Supersedes the legacy Job Condition node; flows made with the old node are migrated automatically."),
            GatePorts,
            new[] { "Double-click to pick the gauge field, comparison and value." },
            () => new FlowNode { Type = NodeType.GaugeCondition, OutputCount = 2 }),

        new(NodeType.StatusCondition, "Status Condition", CatCondition, FontAwesomeIcon.Magic,
            "Checks a buff or debuff on you or your current target: present, seconds remaining, or stacks.",
            Append(GateHow,
                "The scope toggle picks whose status list is read (yours or the current target's), and " +
                "the source toggle picks whether only statuses you applied count, or anyone's — useful " +
                "for reading raid buffs cast by other players."),
            GatePorts,
            new[] { "The node shows the picked status icon once a status is chosen." },
            () => new FlowNode { Type = NodeType.StatusCondition, OutputCount = 2 }),

        new(NodeType.CooldownCondition, "Cooldown Condition", CatCondition, FontAwesomeIcon.Hourglass,
            "Checks another action's cooldown: ready, seconds remaining or elapsed, charges, or level met.",
            Append(GateHow,
                "The checked action doesn't have to appear anywhere in the flow — you can gate a chain " +
                "on any action's cooldown state, e.g. only burst when a raid buff is off cooldown."),
            GatePorts,
            new[] { "\"Elapsed\" is handy for lining up with buff timers: elapsed ≥ X means it was used at least X seconds ago." },
            () => new FlowNode { Type = NodeType.CooldownCondition, OutputCount = 2 }),

        new(NodeType.TargetCondition, "Target Condition", CatCondition, FontAwesomeIcon.Crosshairs,
            "Checks your current target: HP %, distance, positionals, casting, boss check, enemy counts.",
            Append(GateHow,
                "With no target, target-dependent checks fail closed — the false (red) output is taken, " +
                "even for negated checks."),
            GatePorts,
            new[] { "Positional checks (front/flank/rear) pair well with \"Target needs positionals\" to skip True North logic on omni bosses." },
            () => new FlowNode { Type = NodeType.TargetCondition, OutputCount = 2 }),

        new(NodeType.PlayerCondition, "Player Condition", CatCondition, FontAwesomeIcon.User,
            "Checks your own state: HP/MP, combat and combat time, movement, countdown, limit break, stance, pet, aggro, duty/FATE.",
            Append(GateHow,
                "Movement checks (is moving, time moving, time stood still) are the usual way to pick " +
                "between instant and hardcast chains on caster jobs."),
            GatePorts,
            new[] { "\"Countdown remaining\" lets an opener chain pre-position before pull." },
            () => new FlowNode { Type = NodeType.PlayerCondition, OutputCount = 2 }),

        new(NodeType.PartyCondition, "Party Condition", CatCondition, FontAwesomeIcon.Users,
            "Checks your party: average HP %, members with a buff, dead members, party in combat.",
            Append(GateHow,
                "When solo, party checks read just you."),
            GatePorts,
            new[] { "\"Members with buff\" counts how many party members currently have a chosen status — good for AoE heal/mitigation gates." },
            () => new FlowNode { Type = NodeType.PartyCondition, OutputCount = 2 }),

        new(NodeType.ActionHistoryCondition, "Action History Condition", CatCondition, FontAwesomeIcon.History,
            "Checks your own recent casts: was the last action X, time since used, uses this combat, last category.",
            Append(GateHow,
                "History comes from what you actually cast (tracked per combat), so it also sees casts " +
                "made outside this flow — manual presses included."),
            GatePorts,
            new[] { "\"Last was weaponskill / spell / ability\" gates on the action category of your most recent cast." },
            () => new FlowNode { Type = NodeType.ActionHistoryCondition, OutputCount = 2 }),

        new(NodeType.LogicCondition, "Logic Condition", CatLogic, FontAwesomeIcon.Microchip,
            "Combines several wired conditions with a boolean expression like (1 AND 2) OR !3.",
            Append(GateHow,
                "The numbered ports on the left are condition inputs. Wire any condition's (or " +
                "another Logic node's) output into them: the true (green) port feeds the condition's " +
                "value, the false (red) port feeds the negated value. Conditions wired this way act " +
                "as pure inputs — they don't route flow themselves. Unwired inputs count as false.",
                "The expression references inputs by number (1..N) and supports AND, OR, NOT, XOR — " +
                "or && || ! ^ — with parentheses, case-insensitive. An invalid expression fails " +
                "closed (the red output is taken) and the node's label turns red.",
                "Logic nodes can feed other Logic nodes for nested combinations."),
            new[] {
                new WikiPort("In (top-left, grey)", "Flow input — the previous node in the chain; a flow wire can also be dropped anywhere on the body."),
                new WikiPort("In 1..N (left)",      "Condition inputs, wired from condition or Logic outputs."),
                new WikiPort("Out 1 (green)", "Taken while the expression is true."),
                new WikiPort("Out 2 (red)",   "Taken while the expression is false."),
            },
            new[] {
                "Double-click to edit the input count and expression.",
                "One condition output can fan out to several Logic nodes.",
            },
            () => new FlowNode {
                Type = NodeType.LogicCondition, OutputCount = 2,
                LogicInputCount = 3, LogicExpr = "(1 AND 2) OR !3",
            }),

        new(NodeType.KeybindCondition, "Keybind Condition", CatLogic, FontAwesomeIcon.Keyboard,
            "True while you hold a chosen key — Shift, Ctrl or Alt.",
            Append(GateHow,
                "Hold the key to take the green side, release for the red side. The classic use is " +
                "a held modifier switching a chain between its single-target and AoE variants on the " +
                "same button.",
                "The key is only read while the game window has focus."),
            GatePorts,
            new[] { "Double-click to pick the key." },
            () => new FlowNode { Type = NodeType.KeybindCondition, OutputCount = 2, CheckParamId = 16 }),

        new(NodeType.ToggleCondition, "Toggle Condition", CatLogic, FontAwesomeIcon.ToggleOn,
            "A manual on/off switch: routes green while ON, red while OFF.",
            Append(GateHow,
                "Flip it by right-clicking the node (Switch On/Off), from its edit dialog, or with " +
                "the chat command \"/excombo toggle <name>\" — handy on a macro button. The state is " +
                "saved with your flows, so it survives reloads.",
                "Several Toggle nodes can share the same name; the command flips them together."),
            GatePorts,
            new[] {
                "Give it a short name (e.g. \"Burst saver\") so the command is easy to type.",
                "The edit dialog has a copy button — paste the command straight into a game macro.",
                "The node's glyph shows the current state: bright = ON, dim = OFF.",
            },
            () => new FlowNode { Type = NodeType.ToggleCondition, OutputCount = 2, ToggleOn = true, ActionLabel = "Burst saver" }),

        new(NodeType.LatchCondition, "Latch", CatLogic, FontAwesomeIcon.Lock,
            "Set/reset memory: turns true when SET fires and stays true until RESET fires.",
            Append(GateHow,
                "Wire condition outputs into the two inputs on the left: S = set, R = reset (reset " +
                "wins if both are true). Once SET has been true for a single moment, the latch holds " +
                "true — even after the condition that set it goes false — until RESET fires.",
                "Typical use: SET on \"burst buff present\", RESET on the false port of \"in combat\" — " +
                "the latch then marks the whole burst window instead of flickering with the buff.",
                "The latch state is runtime-only: it clears when you edit the flow, and can be " +
                "cleared manually via right-click → Reset Latch State."),
            new[] {
                new WikiPort("In (top-left, grey)", "Flow input — the previous node in the chain; a flow wire can also be dropped anywhere on the body."),
                new WikiPort("S / R (left)",        "Set and Reset signal inputs, wired from condition or Logic outputs (false port = negated)."),
                new WikiPort("Out 1 (green)",       "Taken while the latch holds true."),
                new WikiPort("Out 2 (red)",         "Taken while the latch is false."),
            },
            new[] { "Reset has priority over Set when both fire in the same moment." },
            () => new FlowNode { Type = NodeType.LatchCondition, OutputCount = 2 }),

        // ── Utility ──────────────────────────────────────────────────────
        new(NodeType.Note, "Note", CatUtility, FontAwesomeIcon.StickyNote,
            "A free-text comment box on the canvas. The executor ignores it completely.",
            new[] {
                "Use notes to label sections of a big flow — opener, burst window, filler — or to " +
                "leave reminders for future you. Notes have no ports and never affect execution.",
            },
            Array.Empty<WikiPort>(),
            new[] {
                "Double-click to edit the text.",
                "Drag the bottom-right triangle to resize.",
            },
            () => new FlowNode { Type = NodeType.Note, NoteText = "Any comment — opener, burst window, reminders…", NoteW = 150f, NoteH = 64f }),
    };

    private static string[] Append(string[] baseText, params string[] extra) {
        var r = new string[baseText.Length + extra.Length];
        baseText.CopyTo(r, 0);
        extra.CopyTo(r, baseText.Length);
        return r;
    }
}
