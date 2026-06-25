using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ExCombo.Windows;

internal static class Style {
    internal const int ColorCount = 28;
    internal const int VarCount   = 8;

    internal static void Push() {
        var v4bg1      = new Vector4(0.102f, 0.106f, 0.118f, 1f);
        var v4bg2      = new Vector4(0.145f, 0.149f, 0.169f, 1f);
        var v4bg3      = new Vector4(0.173f, 0.180f, 0.200f, 1f);
        var v4text1    = new Vector4(0.827f, 0.831f, 0.839f, 1f);
        var v4text2    = new Vector4(0.565f, 0.573f, 0.588f, 1f);
        var v4accent   = new Vector4(0.455f, 0.765f, 1.000f, 1f);
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
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,        new Vector4(0.455f, 0.765f, 1.000f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,         new Vector4(0.455f, 0.765f, 1.000f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.Header,               new Vector4(0.455f, 0.765f, 1.000f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,        v4bg3);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,         new Vector4(0.455f, 0.765f, 1.000f, 0.22f));
        ImGui.PushStyleColor(ImGuiCol.Separator,            v4border);
        ImGui.PushStyleColor(ImGuiCol.CheckMark,            v4accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,           v4accent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          v4bg1);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        v4scrollT);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, v4accent);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              v4bg1);
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight,     v4border);

        // ── Style vars ───────────────────────────────────────────────
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,    8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(14f, 12f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,     5f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,     8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,     5f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,      4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 5f);
    }

    internal static void Pop() {
        ImGui.PopStyleVar(VarCount);
        ImGui.PopStyleColor(ColorCount);
    }
}
