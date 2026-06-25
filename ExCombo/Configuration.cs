using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using ExCombo.Flow;

namespace ExCombo;

[Serializable]
public class Configuration : IPluginConfiguration {
    public int Version { get; set; } = 1;
    public List<ComboFlow> Flows { get; set; } = new();

    public bool  ShowOverlay  { get; set; } = true;
    public float OverlayX     { get; set; } = 100f;
    public float OverlayY     { get; set; } = 100f;
    public float OverlayScale { get; set; } = 1f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
