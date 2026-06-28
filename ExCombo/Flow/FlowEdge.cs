using System;

namespace ExCombo.Flow;

public class FlowEdge {
    public string Id             { get; set; } = Guid.NewGuid().ToString();
    public string FromNodeId     { get; set; } = "";
    public string ToNodeId       { get; set; } = "";
    public int    FromPortIndex  { get; set; } = 0;
}
