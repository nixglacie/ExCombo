using System;

namespace ExCombo.Flow;

public class FlowEdge {
    public string Id             { get; set; } = Guid.NewGuid().ToString();
    public string FromNodeId     { get; set; } = "";
    public string ToNodeId       { get; set; } = "";
    public int    FromPortIndex  { get; set; } = 0;
    // 0 = normal flow edge into the node's single flow input (all pre-Logic edges deserialize
    // to this). >= 1 = predicate edge into a Logic node's numbered input slot; excluded from
    // flow traversal and unique per (ToNodeId, ToPortIndex) instead of per source port.
    public int    ToPortIndex    { get; set; } = 0;
}
