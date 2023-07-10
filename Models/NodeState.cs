using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class NodeState
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public Point3D Location { get; set; }
    public Floor[]? Floors { get; set; }
}

public class NodeTeleState:NodeState
{
    public NodeTelemetry? Telemetry { get; set; }
}