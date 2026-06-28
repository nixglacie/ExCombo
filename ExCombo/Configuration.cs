using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using ExCombo.Flow;

namespace ExCombo;

[Serializable]
public class Configuration : IPluginConfiguration {
    public int            Version      { get; set; } = 1;
    public List<ComboFlow> Flows       { get; set; } = new();
    public bool           ShowDtrEntry { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
