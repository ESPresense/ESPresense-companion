using System.Collections.Concurrent;
using ESPresense.Converters;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Models;

public class NodeState
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(Point3DConverter))]
    public Point3D Location { get; set; }
    public string[]? Floors { get; set; }

    [System.Text.Json.Serialization.JsonConverter(typeof(NodeToNodeConverter))]
    public ConcurrentDictionary<string, NodeToNode> Nodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
}

public class NodeStateTele : NodeState
{
    public NodeTelemetry? Telemetry { get; set; }
    public bool Online { get; set; }
    public Flavor? Flavor { get; set; }
    public CPU? CPU { get; set; }
}