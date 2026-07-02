using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using ExCombo.Flow;

namespace ExCombo.Windows;

internal static class Style {
    internal const int ColorCount = 28;
    internal const int VarCount   = 9;

    // ── Accent (user-tunable via Configuration.AccentColor) ──────────────
    internal static Vector4 AccentColor {
        get {
            var a = Plugin.Config?.AccentColor;
            return a is { Length: >= 3 } ? new Vector4(a[0], a[1], a[2], 1f)
                                         : new Vector4(0.455f, 0.765f, 1.000f, 1f);
        }
    }
    internal static Vector4 Accent(float alpha) {
        var a = AccentColor;
        return new Vector4(a.X, a.Y, a.Z, alpha);
    }
    internal static uint AccentU32(float alpha = 1f) => ImGui.ColorConvertFloat4ToU32(Accent(alpha));
    internal static Vector4 AccentHover {
        get { var a = AccentColor; return new Vector4(MathF.Min(a.X + 0.14f, 1f), MathF.Min(a.Y + 0.07f, 1f), a.Z, 1f); }
    }
    internal static Vector4 AccentActive {
        get { var a = AccentColor; return new Vector4(a.X * 0.77f, a.Y * 0.85f, a.Z * 0.9f, 1f); }
    }

    // ── Per-node-type colors (user-tunable via Configuration) ────────────
    internal static Vector4 NodeColor(NodeType t) {
        float[]? a;
        Vector4 def;
        switch (t) {
            case NodeType.Trigger: a = Plugin.Config?.NodeColorTrigger; def = new Vector4(0.635f, 0.855f, 0.549f, 1f); break;
            case NodeType.Action:  a = Plugin.Config?.NodeColorAction;  def = new Vector4(0.455f, 0.765f, 1.000f, 1f); break;
            case NodeType.Branch:  a = Plugin.Config?.NodeColorBranch;  def = new Vector4(0.700f, 0.400f, 1.000f, 1f); break;
            case NodeType.Note:    a = Plugin.Config?.NodeColorNote;    def = new Vector4(1f, 1f, 1f, 1f); break;
            case NodeType.LogicCondition:
            case NodeType.LatchCondition:
            case NodeType.KeybindCondition:
            case NodeType.ToggleCondition:
                                   a = Plugin.Config?.NodeColorLogic;   def = new Vector4(0.950f, 0.840f, 0.350f, 1f); break;
            default:
                if (FlowNode.IsGate(t)) { a = Plugin.Config?.NodeColorCondition; def = new Vector4(0.900f, 0.630f, 0.310f, 1f); }
                else                    { a = null;                              def = new Vector4(1f, 1f, 1f, 1f); }
                break;
        }
        return a is { Length: >= 3 } ? new Vector4(a[0], a[1], a[2], 1f) : def;
    }
    internal static Vector4 NodeColor(NodeType t, float alpha) { var v = NodeColor(t); return new Vector4(v.X, v.Y, v.Z, alpha); }
    internal static uint NodeColU32(NodeType t, float alpha = 1f) => ImGui.ColorConvertFloat4ToU32(NodeColor(t, alpha));

    internal static Vector4 ComboColor {
        get {
            var a = Plugin.Config?.ComboGroupColor;
            return a is { Length: >= 3 } ? new Vector4(a[0], a[1], a[2], 1f) : new Vector4(1f, 0.7f, 0.2f, 1f);
        }
    }
    internal static uint ComboU32(float alpha = 1f) { var a = ComboColor; return ImGui.ColorConvertFloat4ToU32(new Vector4(a.X, a.Y, a.Z, alpha)); }

    // Status-badge colors (oGCD / retarget). Combo-group badge uses ComboU32.
    internal static uint BadgeOgcdU32(float alpha = 1f)     => ArrU32(Plugin.Config?.BadgeOgcdColor,     1f, 0.85f, 0.2f, alpha);
    internal static uint BadgeRetargetU32(float alpha = 1f) => ArrU32(Plugin.Config?.BadgeRetargetColor, 0.4f, 0.85f, 1f, alpha);
    internal static uint BadgeComboU32(float alpha = 1f)    => ArrU32(Plugin.Config?.BadgeComboColor,    1f, 0.7f, 0.2f, alpha);
    private static uint ArrU32(float[]? a, float dr, float dg, float db, float alpha) {
        var c = a is { Length: >= 3 } ? new Vector4(a[0], a[1], a[2], alpha) : new Vector4(dr, dg, db, alpha);
        return ImGui.ColorConvertFloat4ToU32(c);
    }

    internal static void Push() {
        var v4bg1      = new Vector4(0.102f, 0.106f, 0.118f, 1f);
        var v4bg2      = new Vector4(0.145f, 0.149f, 0.169f, 1f);
        var v4bg3      = new Vector4(0.173f, 0.180f, 0.200f, 1f);
        var v4text1    = new Vector4(0.827f, 0.831f, 0.839f, 1f);
        var v4text2    = new Vector4(0.565f, 0.573f, 0.588f, 1f);
        var a          = Plugin.Config?.AccentColor;
        var v4accent   = a is { Length: >= 3 } ? new Vector4(a[0], a[1], a[2], 1f)
                                               : new Vector4(0.455f, 0.765f, 1.000f, 1f);
        var v4borderB  = new Vector4(0.333f, 0.353f, 0.388f, 1f);
        var v4border   = new Vector4(0.216f, 0.227f, 0.251f, 1f);
        var v4scrollT  = new Vector4(0.384f, 0.416f, 0.478f, 1f);
        var v4titleAct = new Vector4(0.145f, 0.149f, 0.169f, 1f);  // bg2

        // ── Window chrome ────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.WindowBg,           v4bg1);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,            v4bg2);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,      v4titleAct);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed,   v4bg1);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,         v4borderB);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,  v4accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,   v4accent);

        // ── Widgets ──────────────────────────────────────────────────
        ImGui.PushStyleColor(ImGuiCol.Text,                 v4text1);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled,         v4text2);
        ImGui.PushStyleColor(ImGuiCol.PopupBg,              new Vector4(0.145f, 0.149f, 0.169f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border,               v4borderB);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,              v4bg1);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,       v4bg3);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,        v4bg3);
        ImGui.PushStyleColor(ImGuiCol.Button,               v4bg3);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,        Accent(v4accent, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,         Accent(v4accent, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.Header,               Accent(v4accent, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,        v4bg3);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,         Accent(v4accent, 0.22f));
        ImGui.PushStyleColor(ImGuiCol.Separator,            v4border);
        ImGui.PushStyleColor(ImGuiCol.CheckMark,            v4accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,           v4accent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          v4bg1);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        v4scrollT);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, v4accent);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              v4bg1);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight,     v4border);

        // Modal dim is rendered at frame-end (after Pop), so a pushed color never applies — write the
        // persistent style value directly instead. Solid black behind our modals.
        ImGui.GetStyle().Colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, 0.4f);

        // ── Style vars ───────────────────────────────────────────────
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,    8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,     5f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,     8f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,   1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,     5f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,      4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 5f);
    }

    private static Vector4 Accent(Vector4 a, float alpha) => new(a.X, a.Y, a.Z, alpha);

    internal static void Pop() {
        ImGui.PopStyleVar(VarCount);
        ImGui.PopStyleColor(ColorCount);
    }
}
