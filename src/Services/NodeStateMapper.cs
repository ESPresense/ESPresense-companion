using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Builds the <see cref="NodeState"/> / <see cref="NodeStateTele"/> DTOs returned by the state API and MCP resources.
/// </summary>
/// <remarks>
/// Explicit mapping (no reflection-based mapper). Field-for-field:
/// - Id, Name, Location and SourceType are copied from the node.
/// - Floors is the array of floor ids (null when the node has no floors).
/// - Nodes receives the same <see cref="NodeToNode"/> instances as the source node.
/// - NodeStateTele additionally carries telemetry, firmware flavor/CPU and online status from the stores.
/// </remarks>
public class NodeStateMapper
{
    private readonly NodeTelemetryStore _nts;
    private readonly FirmwareTypeStore _fs;

    public NodeStateMapper(NodeTelemetryStore nts, FirmwareTypeStore fs)
    {
        _nts = nts;
        _fs = fs;
    }

    public static NodeState ToNodeState(Node src)
    {
        var dest = new NodeState();
        CopyBase(src, dest);
        return dest;
    }

    public NodeStateTele ToNodeStateTele(Node src)
    {
        var dest = new NodeStateTele();
        CopyBase(src, dest);
        dest.Telemetry = _nts.Get(src.Id);
        if (dest.Telemetry != null)
        {
            dest.Flavor = _fs.GetFlavor(dest.Telemetry.Firmware);
            dest.CPU = _fs.GetCpu(dest.Telemetry.Firmware);
        }
        dest.Online = _nts.Online(src.Id);
        return dest;
    }

    public static List<NodeState> ToNodeStates(IEnumerable<Node> nodes) => nodes.Select(ToNodeState).ToList();

    public List<NodeStateTele> ToNodeStateTeles(IEnumerable<Node> nodes) => nodes.Select(ToNodeStateTele).ToList();

    private static void CopyBase(Node src, NodeState dest)
    {
        dest.Id = src.Id;
        dest.Name = src.Name;
        dest.Location = src.Location;
        dest.Floors = src.Floors?.Select(f => f.Id!).ToArray();
        dest.SourceType = src.SourceType;
        foreach (var kv in src.Nodes)
            dest.Nodes[kv.Key] = kv.Value;
    }
}
