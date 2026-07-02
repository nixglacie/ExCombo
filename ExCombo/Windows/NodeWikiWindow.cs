using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using ExCombo.Flow;
using ExCombo.Helpers;

namespace ExCombo.Windows;

// Node reference: every node type drawn in the editor's visual style next to a description of what
// it does. Content comes from NodeWikiCatalog; condition check tables are built live from
// ConditionCatalog so they stay in sync with the editor.
public class NodeWikiWindow : Window, IDisposable {
    // Editor node geometry (FlowEditorWindow), drawn here at a fixed readability scale.
    private const float NodeW  = 64f;
    private const float SlotH  = 32f;
    private const float PortR  = 6f;
    private const float Scale  = 1.3f;
    private const float CellW  = 215f;   // fixed preview column width so entries align

    // Large FontAwesome handle for node body glyphs (same delegate pattern as FlowEditorWindow).
    private const float GlyphPx = 32f;
    private IFontHandle? _iconFontLarge;
    private IFontHandle IconFontLarge => _iconFontLarge ??=
        Plugin.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk => tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = GlyphPx })));

    public NodeWikiWindow() : base("Node Wiki###ExComboWiki") {
        SizeConstraints = new WindowSizeConstraints {
            MinimumSize = new Vector2(560, 400),
            MaximumSize = new Vector2(900, 2000),
        };
        Size          = new Vector2(680, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() {
        _iconFontLarge?.Dispose();
        _iconFontLarge = null;
    }

    public override void PreDraw()  => Style.Push();
    public override void PostDraw() => Style.Pop();

    private static uint Col(float r, float g, float b, float a = 1f) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private const float CardPad = 10f;   // inner padding of an entry card
    private const float CardGap = 8f;    // vertical gap between cards

    public override void Draw() {
        ImGui.TextDisabled("What every node does and how it behaves. Right-click the editor canvas to add nodes.");
        ImGui.Spacing();

        string? cat = null;
        foreach (var e in NodeWikiCatalog.Entries) {
            if (e.Category != cat) {
                cat = e.Category;
                DrawCategoryHeader(cat);
            }
            DrawEntry(e);
        }
    }

    private static void DrawCategoryHeader(string cat) {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Style.AccentColor, cat.ToUpperInvariant());
        var dl = ImGui.GetWindowDrawList();
        var p  = ImGui.GetCursorScreenPos();
        var w  = ImGui.GetContentRegionAvail().X;
        dl.AddRectFilled(p, p + new Vector2(w, 2f), Style.AccentU32(0.55f));
        ImGui.Dummy(new Vector2(1f, 8f));
    }

    // One node entry, drawn as a card: bg panel + border + accent stripe in the node's color.
    // Content renders on drawlist channel 1 so the card bg (channel 0) can be sized to the
    // final content rect afterwards yet still land behind it.
    private void DrawEntry(WikiEntry e) {
        var node = e.Template();
        var dl   = ImGui.GetWindowDrawList();

        var cardMin   = ImGui.GetCursorScreenPos();
        var availW    = ImGui.GetContentRegionAvail().X;
        var wrapLocal = ImGui.GetCursorPosX() + availW - CardPad;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(cardMin + new Vector2(CardPad + 3f, CardPad));
        ImGui.BeginGroup();

        // ── Preview floated top-left; text flows around it (CSS float-left) ──
        var bodySize   = BodySize(node) * Scale;
        var cellH      = 22f + bodySize.Y + 16f;   // label above + node + badge overhang below
        var contentMin = ImGui.GetCursorScreenPos();
        DrawNodePreview(dl, contentMin + new Vector2(18f, 22f), e, node, bodySize);

        _baseLeftLocal  = ImGui.GetCursorPosX();
        _floatLeftLocal = _baseLeftLocal + CellW;
        _floatBottomY   = contentMin.Y + cellH;
        _wrapRightLocal = wrapLocal;

        ImGui.SetCursorPosX(CurLeft());
        ImGui.TextColored(Style.NodeColor(e.Type), e.Name);
        FlowText(e.Summary);
        ImGui.Spacing();
        foreach (var p in e.HowItWorks) {
            FlowText(p);
            ImGui.Spacing();
        }

        if (e.Ports.Length > 0) {
            ImGui.SetCursorPosX(CurLeft());
            ImGui.TextDisabled("Ports");
            foreach (var p in e.Ports)
                FlowText($"{p.Label} — {p.Description}");
            ImGui.Spacing();
        }

        if (e.Tips.Length > 0) {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            foreach (var t in e.Tips)
                FlowText($"• {t}");
            ImGui.PopStyleColor();
        }

        // Logic/Keybind/Toggle/Latch have no ConditionCatalog checks table.
        if (FlowNode.IsGate(e.Type) && e.Type is not (NodeType.LogicCondition or NodeType.KeybindCondition
                                                   or NodeType.ToggleCondition or NodeType.LatchCondition)) {
            ImGui.SetCursorPosX(CurLeft());
            DrawChecksTable(e);
        }

        // If the text ended beside the preview, pad the card down to the preview's bottom.
        var floatRem = _floatBottomY - ImGui.GetCursorScreenPos().Y;
        if (floatRem > 0f) ImGui.Dummy(new Vector2(1f, floatRem));

        ImGui.EndGroup();

        // ── Card chrome behind the content ────────────────────────────────
        var cardMax = new Vector2(cardMin.X + availW, ImGui.GetItemRectMax().Y + CardPad);
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(cardMin, cardMax, Col(0.129f, 0.133f, 0.149f), 6f);
        dl.AddRect(cardMin, cardMax, Style.NodeColU32(e.Type, 0.30f), 6f, ImDrawFlags.None, 1f);
        dl.AddRectFilled(cardMin, new Vector2(cardMin.X + 3f, cardMax.Y),
            Style.NodeColU32(e.Type, 0.85f), 6f, ImDrawFlags.RoundCornersLeft);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(cardMin.X, cardMax.Y));
        ImGui.Dummy(new Vector2(1f, CardGap));
    }

    // ── Float-left text flow with inline status badges ────────────────────
    // Lines that sit beside the floated node preview start at _floatLeftLocal; lines below it
    // reclaim the full card width from _baseLeftLocal (CSS float-left behaviour). Paragraphs are
    // laid out word by word so {ogcd}/{retarget}/{combo} tokens can render as real editor badges;
    // token-free paragraphs fully below the preview take the cheap native wrap path.

    private float _baseLeftLocal;    // card content left edge (window-local X)
    private float _floatLeftLocal;   // text left edge while beside the preview
    private float _floatBottomY;     // screen Y where the floated preview ends
    private float _wrapRightLocal;   // right wrap edge (window-local X)

    private float CurLeft() =>
        ImGui.GetCursorScreenPos().Y < _floatBottomY ? _floatLeftLocal : _baseLeftLocal;

    private void FlowText(string text) {
        if (!text.Contains('{') && ImGui.GetCursorScreenPos().Y >= _floatBottomY) {
            ImGui.PushTextWrapPos(_wrapRightLocal);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            return;
        }

        var spaceW = ImGui.CalcTextSize(" ").X;
        var winX   = ImGui.GetWindowPos().X - ImGui.GetScrollX();
        // Tighten vertical spacing so manual lines read like one wrapped paragraph.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 1.5f));
        ImGui.SetCursorPosX(CurLeft());
        var first = true;
        foreach (var word in text.Split(' ')) {
            if (word.Length == 0) continue;
            var isBadge = BadgeFor(word, out var badgeBg, out var badgeGlyph, out var suffix);
            var itemW   = isBadge ? ImGui.GetTextLineHeight() + ImGui.CalcTextSize(suffix).X
                                  : ImGui.CalcTextSize(word).X;
            if (!first) {
                // Continue the line if the word fits before the wrap edge; otherwise start a new
                // line at whichever left edge applies at that height.
                var lineEndLocal = ImGui.GetItemRectMax().X - winX;
                if (lineEndLocal + spaceW + itemW <= _wrapRightLocal) ImGui.SameLine(0f, spaceW);
                else ImGui.SetCursorPosX(CurLeft());
            }
            if (isBadge) {
                InlineBadge(badgeBg, badgeGlyph);
                if (suffix.Length > 0) {
                    ImGui.SameLine(0f, 0f);
                    ImGui.TextUnformatted(suffix);
                }
            } else {
                ImGui.TextUnformatted(word);
            }
            first = false;
        }
        ImGui.PopStyleVar();
    }

    // "{ogcd}" / "{retarget}" / "{combo}" with optional trailing punctuation ("{combo},").
    private static bool BadgeFor(string word, out uint bg, out string glyph, out string suffix) {
        bg = 0; glyph = ""; suffix = "";
        if (word.Length < 2 || word[0] != '{') return false;
        var close = word.IndexOf('}');
        if (close < 0) return false;
        suffix = word[(close + 1)..];
        switch (word[..(close + 1)]) {
            case "{ogcd}":     bg = Style.BadgeOgcdU32();     glyph = FontAwesomeIcon.Bolt.ToIconString();       return true;
            case "{retarget}": bg = Style.BadgeRetargetU32(); glyph = FontAwesomeIcon.Crosshairs.ToIconString(); return true;
            case "{combo}":    bg = Style.BadgeComboU32();    glyph = FontAwesomeIcon.Link.ToIconString();       return true;
            default: suffix = ""; return false;
        }
    }

    private const float BadgePad = 2f;

    // Square, text-height replica of the Action node's status badge, usable inline in a sentence.
    // The glyph is drawn at a reduced font size so it always fits the square instead of stretching it.
    private static void InlineBadge(uint bg, string glyph) {
        var side = ImGui.GetTextLineHeight();
        var size = new Vector2(side, side);
        var pos  = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var dl   = ImGui.GetWindowDrawList();
        var dark = Col(0.07f, 0.08f, 0.11f);
        dl.AddRectFilled(pos, pos + size, bg, 4f);
        dl.AddRect(pos, pos + size, dark, 4f, ImDrawFlags.None, 1f);
        ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        var font  = ImGui.GetFont();
        var fSize = ImGui.GetFontSize();
        var gsz   = ImGui.CalcTextSize(glyph);
        var fit   = side - 2f * BadgePad;
        var scale = MathF.Min(1f, MathF.Min(fit / gsz.X, fit / gsz.Y));
        dl.AddText(font, fSize * scale, pos + (size - gsz * scale) * 0.5f, dark, glyph);
        ImGui.PopFont();
    }

    // ── Live check tables from ConditionCatalog ──────────────────────────

    private void DrawChecksTable(WikiEntry e) {
        ImGui.Spacing();
        if (!ImGui.TreeNode($"Available checks##wiki{e.Type}")) return;

        System.Collections.Generic.IReadOnlyList<CheckDef>? checks;
        if (e.Type == NodeType.GaugeCondition) {
            var job = Plugin.ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Abbreviation.ToString();
            checks  = string.IsNullOrEmpty(job) ? null : ConditionCatalog.ForGauge(job);
            if (checks is { Count: 0 }) checks = null;
            if (checks == null)
                ImGui.TextDisabled("Log in on a job with a gauge to see its fields here.");
            else
                ImGui.TextDisabled($"Fields for your current job ({job}); other jobs get their own list.");
        } else {
            checks = ConditionCatalog.For(e.Type);
        }

        if (checks != null && ImGui.BeginTable($"##wikichecks{e.Type}", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp)) {
            ImGui.TableSetupColumn("Check",     ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Result",    ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Scope",     ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableHeadersRow();
            foreach (var d in checks) {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(d.Label);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(d.Bool ? "true / false" : "number");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(d.Param switch {
                    CheckParamKind.Number   => "number",
                    CheckParamKind.ActionId => "action",
                    CheckParamKind.StatusId => "status",
                    CheckParamKind.Range    => "range",
                    _                       => "—",
                });
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ScopeText(d));
            }
            ImGui.EndTable();
        }
        ImGui.TreePop();
    }

    private static string ScopeText(CheckDef d) {
        if (d.HasTarget && d.HasSource) return "self/target, mine/anyone";
        if (d.HasTarget)                return "self/target";
        if (d.RequiresTarget)           return "needs target";
        return "—";
    }

    // ── Static node preview (replicates FlowEditorWindow visuals, no interaction) ──

    private static Vector2 BodySize(FlowNode n) => n.Type == NodeType.Note
        ? new Vector2(n.NoteW, n.NoteH)
        : new Vector2(NodeW, n.Type == NodeType.Branch || FlowNode.IsGate(n.Type)
            ? MathF.Max(NodeW, SlotH * Math.Max(n.OutputCount,
                  n.PredicateInputs() > 0 ? n.PredicateInputs() + 1 : 0))
            : NodeW);

    private void DrawNodePreview(ImDrawListPtr dl, Vector2 sp, WikiEntry e, FlowNode node, Vector2 size) {
        var s     = Scale;
        var w     = size.X;
        var h     = size.Y;
        var portR = PortR * s;
        var accent = Style.NodeColU32(node.Type);

        // Floating label above the body (editor style).
        var label  = node.Type switch {
            NodeType.Branch => "Priority",
            NodeType.Note   => "Note",
            _               => FlowNode.IsGate(node.Type) ? e.Name.Replace(" Condition", "") : node.ActionLabel,
        };
        var labelW = ImGui.CalcTextSize(label).X;
        DrawHelpers.DrawText(dl, sp + new Vector2((w - labelW) * 0.5f, -18f), label, accent, true);

        if (node.Type == NodeType.Note) {
            dl.AddRectFilled(sp, sp + size, Col(0.12f, 0.12f, 0.13f, 0.95f), 6f);
            dl.AddRect(sp, sp + size, Style.NodeColU32(NodeType.Note, 0.45f), 6f, ImDrawFlags.None, 1.5f);
            const float tPad = 6f;
            dl.PushClipRect(sp + new Vector2(tPad, tPad), sp + size - new Vector2(tPad, tPad), true);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), sp + new Vector2(tPad, tPad),
                Col(0.96f, 0.96f, 0.97f), node.NoteText, w - 2f * tPad);
            dl.PopClipRect();
            var hc = sp + size;
            dl.AddTriangleFilled(hc + new Vector2(-12f, -2f), hc + new Vector2(-2f, -2f), hc + new Vector2(-2f, -12f),
                Style.NodeColU32(NodeType.Note, 0.5f));
            return;
        }

        // Body fill matches the editor's per-family tint.
        var bg = node.Type switch {
            NodeType.Trigger        => Col(0.09f, 0.13f, 0.10f),
            NodeType.Action         => Col(0.09f, 0.11f, 0.16f),
            NodeType.Branch         => Col(0.08f, 0.05f, 0.12f),
            NodeType.LogicCondition => Col(0.13f, 0.12f, 0.04f),
            NodeType.LatchCondition => Col(0.13f, 0.12f, 0.04f),
            _                       => Col(0.12f, 0.08f, 0.03f),   // gates
        };
        dl.AddRectFilled(sp, sp + size, bg, 6f);

        // Centered category glyph (editor draws these for Target/Player/Party/History; the wiki
        // uses one for every type since previews have no picked action/status icon).
        if (node.Type != NodeType.Branch) {
            var gstr = e.Glyph.ToIconString();
            using (IconFontLarge.Push()) {
                var font = ImGui.GetFont();
                var sz   = ImGui.GetFontSize();
                var gsz  = ImGui.CalcTextSize(gstr);
                dl.AddText(font, sz, sp + new Vector2((w - gsz.X) * 0.5f, (h - gsz.Y) * 0.5f),
                    Style.NodeColU32(node.Type, 0.85f), gstr);
            }
        }

        dl.AddRect(sp, sp + size, Style.NodeColU32(node.Type, 0.5f), 6f, ImDrawFlags.None, 1.5f);

        // Input ports — Triggers have none; Logic/Latch stack the flow input (grey ring, top) and
        // their labeled predicate slots down the left side.
        if (node.PredicateInputs() > 0) {
            var isLatchPrev = node.Type == NodeType.LatchCondition;
            for (var slot = 0; slot <= node.PredicateInputs(); slot++) {
                var pos = sp + new Vector2(0f, (slot + 0.5f) * SlotH * Scale);
                dl.AddCircleFilled(pos, portR, Col(0.25f, 0.25f, 0.35f));
                var ring = slot == 0 ? Col(0.45f, 0.45f, 0.60f) : Style.NodeColU32(node.Type, 0.9f);
                dl.AddCircle(pos, portR, ring, 12, 1.5f);
                if (slot >= 1)
                    DrawHelpers.DrawText(dl, pos + new Vector2(portR + 4f, -ImGui.GetFontSize() * 0.5f),
                        isLatchPrev ? (slot == 1 ? "S" : "R") : slot.ToString(),
                        Style.NodeColU32(node.Type, 0.8f), false);
            }
        } else if (node.Type != NodeType.Trigger) {
            var inPort = sp + new Vector2(0f, h * 0.5f);
            dl.AddCircleFilled(inPort, portR, Col(0.25f, 0.25f, 0.35f));
            dl.AddCircle(inPort, portR, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
        }

        if (node.Type == NodeType.Branch || FlowNode.IsGate(node.Type)) {
            var isGate = FlowNode.IsGate(node.Type);
            for (var p = 0; p < node.OutputCount; p++) {
                var portPos = sp + new Vector2(w, (p + 0.5f) * SlotH * s);
                dl.AddCircleFilled(portPos, portR, Col(0.25f, 0.25f, 0.35f));
                if (isGate) {
                    // Port 0 = true (green), port 1 = false (red) — same rings as the editor.
                    var ring = p == 0 ? Col(0.30f, 0.78f, 0.30f) : Col(0.80f, 0.28f, 0.28f);
                    dl.AddCircle(portPos, portR, ring, 12, 1.5f);
                    var txt = p == 0 ? "true" : "false";
                    DrawHelpers.DrawText(dl, portPos + new Vector2(portR + 5f, -ImGui.GetFontSize() * 0.5f), txt, ring, true);
                } else {
                    dl.AddCircle(portPos, portR, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
                    var numLabel = (p + 1).ToString();
                    var numW     = ImGui.CalcTextSize(numLabel).X;
                    DrawHelpers.DrawText(dl, portPos + new Vector2(-numW - portR - 4f, -ImGui.GetFontSize() * 0.5f),
                        numLabel, Style.NodeColU32(node.Type, 0.8f), false);
                    if (p < node.OutputCount - 1) {
                        var lineY = sp.Y + (p + 1) * SlotH * s;
                        dl.AddLine(new Vector2(sp.X + 4f, lineY), new Vector2(sp.X + w - 4f, lineY),
                            Style.NodeColU32(node.Type, 0.2f), 1f);
                    }
                }
            }
        } else {
            var outPort = sp + new Vector2(w, h * 0.5f);
            dl.AddCircleFilled(outPort, portR, Col(0.25f, 0.25f, 0.35f));
            dl.AddCircle(outPort, portR, Col(0.45f, 0.45f, 0.60f), 12, 1.5f);
        }

        // Status badges centered on the bottom edge (Action preview shows all three).
        if (node.Type == NodeType.Action &&
            (node.IsOgcd || node.GroupId != null || node.RetargetPriority.Count > 0)) {
            ImGui.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            const float pad = 3f, gap = 4f;
            var darkCol = Col(0.07f, 0.08f, 0.11f);

            var boltStr = FontAwesomeIcon.Bolt.ToIconString();
            var linkStr = FontAwesomeIcon.Link.ToIconString();
            var rtgStr  = FontAwesomeIcon.Crosshairs.ToIconString();
            var boltGsz = ImGui.CalcTextSize(boltStr);
            var linkGsz = ImGui.CalcTextSize(linkStr);
            var rtgGsz  = ImGui.CalcTextSize(rtgStr);
            var boltW   = boltGsz.X + 2f * pad;
            var linkW   = linkGsz.X + 2f * pad;
            var rtgW    = rtgGsz.X + 2f * pad;

            var hasBolt = node.IsOgcd;
            var hasLink = node.GroupId != null;
            var hasRtg  = node.RetargetPriority.Count > 0;
            var nBadges = (hasBolt ? 1 : 0) + (hasLink ? 1 : 0) + (hasRtg ? 1 : 0);
            var totalW  = (hasBolt ? boltW : 0f) + (hasLink ? linkW : 0f) + (hasRtg ? rtgW : 0f)
                        + (nBadges > 1 ? (nBadges - 1) * gap : 0f);
            var x       = sp.X + (w - totalW) * 0.5f;

            void Badge(string glyph, Vector2 gsz, float bw, uint bgCol, float gdx) {
                var bMin = new Vector2(x, sp.Y + h - (gsz.Y + 2f * pad) * 0.5f);
                var bMax = bMin + new Vector2(bw, gsz.Y + 2f * pad);
                dl.AddRectFilled(bMin, bMax, bgCol, 4f);
                dl.AddRect(bMin, bMax, darkCol, 4f, ImDrawFlags.None, 1.5f);
                dl.AddText(bMin + new Vector2(pad + gdx, pad - 1f), darkCol, glyph);
                x += bw + gap;
            }

            if (hasBolt) Badge(boltStr, boltGsz, boltW, Style.BadgeOgcdU32(), 0f);
            if (hasLink) Badge(linkStr, linkGsz, linkW, Style.BadgeComboU32(), 0.5f);
            if (hasRtg)  Badge(rtgStr,  rtgGsz,  rtgW,  Style.BadgeRetargetU32(), 1f);
            ImGui.PopFont();
        }
    }
}
