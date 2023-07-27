using ESPresense.Converters;
using MathNet.Spatial.Euclidean;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

public class NodeState
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    [JsonConverter(typeof(Point3DConverter))]
    public Point3D Location { get; set; }
    public string[]? Floors { get; set; }
}

public class NodeStateTele : NodeState
{
    public NodeTelemetry? Telemetry { get; set; }
}