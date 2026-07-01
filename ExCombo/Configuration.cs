using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using ExCombo.Flow;

namespace ExCombo;

public enum LogLevel {
    Off     = 0,   // errors only
    Verbose = 1,   // full per-transition debug spam
}

public enum WireStyle {
    Curved   = 0,   // Bézier
    Straight = 1,   // direct line
}

[Serializable]
public class Configuration : IPluginConfiguration {
    public int             Version      { get; set; } = 1;
    public List<ComboFlow> Flows        { get; set; } = new();
    public bool            ShowDtrEntry { get; set; } = true;

    // ── Master switch ────────────────────────────────────────────────────
    // When false, every flow reverts to vanilla icons without touching per-flow Enabled.
    public bool Enabled { get; set; } = true;

    // ── Behaviour tuning (was hard-coded in FlowExecutor / WeaveHelper) ───
    public int   MaxWeavesPerGcd   { get; set; } = 2;      // oGCDs allowed per GCD window (1–3)
    public float AnimLockBudget    { get; set; } = 0.6f;   // assumed animation lock (s)
    public float QueueBudget       { get; set; } = 0.5f;   // action-queue lead time (s)
    public int   ComboGraceMs      { get; set; } = 500;    // trust-game-combo grace after a press
    public int   ChainResetSeconds { get; set; } = 15;     // abandon+reset chain after inactivity

    // ── Safety gates (suppress replacement in these states) ──────────────
    public bool DisableInPvP       { get; set; } = false;
    public bool PauseWhenOccupied  { get; set; } = true;   // cutscenes, crafting, mounting, between areas…
    public bool ReplaceOnlyInCombat{ get; set; } = false;

    // ── Editor preferences ───────────────────────────────────────────────
    public float GridSize         { get; set; } = 32f;
    public bool  SnapToGrid       { get; set; } = true;
    public bool  ConfirmNodeDelete{ get; set; } = false;
    public int   UndoDepth        { get; set; } = 50;   // editor undo history size
    public WireStyle WireStyle    { get; set; } = WireStyle.Curved;

    // ── Appearance ───────────────────────────────────────────────────────
    // Primary interactive accent (RGBA 0..1). Stored as float[] so Dalamud's serializer round-trips it.
    public float[] AccentColor { get; set; } = { 0.455f, 0.765f, 1.0f, 1f };

    // Per-node-type colors (RGB 0..1). Alpha variants are derived at draw time.
    public float[] NodeColorTrigger   { get; set; } = { 0.635f, 0.855f, 0.549f };
    public float[] NodeColorAction    { get; set; } = { 0.455f, 0.765f, 1.000f };
    public float[] NodeColorBranch    { get; set; } = { 0.700f, 0.400f, 1.000f };
    public float[] NodeColorCondition { get; set; } = { 0.900f, 0.630f, 0.310f };
    public float[] NodeColorNote       { get; set; } = { 1.000f, 1.000f, 1.000f };
    public float[] ComboGroupColor     { get; set; } = { 1.000f, 0.700f, 0.200f };
    public float[] BadgeOgcdColor      { get; set; } = { 1.000f, 0.850f, 0.200f };
    public float[] BadgeRetargetColor  { get; set; } = { 0.400f, 0.850f, 1.000f };
    public float[] BadgeComboColor     { get; set; } = { 1.000f, 0.700f, 0.200f };

    // ── Diagnostics ──────────────────────────────────────────────────────
    public LogLevel LogLevel            { get; set; } = LogLevel.Verbose;
    public bool     ShowConditionState  { get; set; } = false;   // tint gate borders live in editor

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
