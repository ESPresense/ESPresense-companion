using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

/// <summary>One node's reading of a device over a <see cref="DeviceWindow"/>.</summary>
public readonly struct DeviceWindowReading
{
    private readonly float _rssi;
    private readonly float _rxAdj;

    /// <param name="nodeId">The node that heard the device.</param>
    /// <param name="rssi">The node's median published rssi over the window (raw - rxAdj).</param>
    /// <param name="rxAdj">The rxAdj the node applied to it; null on firmware that reports neither rxAdj nor rawRssi.</param>
    public DeviceWindowReading(string nodeId, double rssi, double? rxAdj)
    {
        NodeId = nodeId;
        _rssi = (float)rssi;
        _rxAdj = rxAdj is { } adj ? (float)adj : float.NaN;
    }

    public string NodeId { get; }
    public double Rssi => _rssi;
    public double? RxAdj => float.IsNaN(_rxAdj) ? null : _rxAdj;

    /// <summary>The rssi the node would have published with <paramref name="newRxAdj"/>, as <see cref="Measure.GetAdjustedRssi"/>.</summary>
    public double AdjustedRssi(double? newRxAdj) =>
        newRxAdj == null || float.IsNaN(_rxAdj) ? _rssi : _rssi + _rxAdj - newRxAdj.Value;
}

/// <summary>
/// A located device's readings over a few seconds: per node, the median rssi and the rxAdj it was received
/// with, plus the rssi@1m the device was ranged with. No position is stored: the device_residual optimizer
/// solves for one with whatever calibration it is evaluating.
/// </summary>
public sealed class DeviceWindow
{
    public required string DeviceId { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }

    /// <summary>The device's rssi@1m in the window (median over the readings).</summary>
    public required double RefRssi { get; init; }

    public required DeviceWindowReading[] Readings { get; init; }
}

/// <summary>
/// Where the nodes are and what volume the floors cover, captured with the device windows so they can be
/// re-solved without <see cref="State"/>.
/// </summary>
public sealed class DeviceGeometry
{
    public DeviceGeometry(IReadOnlyDictionary<string, Point3D> nodes, IReadOnlyList<(Point3D Min, Point3D Max)> floors)
    {
        Nodes = nodes;
        Floors = floors;
        Signature = string.Join(";", nodes.OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase).Select(n => $"{n.Key}@{n.Value.X:0.###},{n.Value.Y:0.###},{n.Value.Z:0.###}"))
                    + "|" + string.Join(";", floors.Select(f => $"{f.Min.X:0.###},{f.Min.Y:0.###},{f.Min.Z:0.###}:{f.Max.X:0.###},{f.Max.Y:0.###},{f.Max.Z:0.###}"));
    }

    /// <summary>Stationary nodes with a configured point, by id (case-insensitive ids, as <see cref="State.Nodes"/>).</summary>
    public IReadOnlyDictionary<string, Point3D> Nodes { get; }

    /// <summary>Each floor's bounding box.</summary>
    public IReadOnlyList<(Point3D Min, Point3D Max)> Floors { get; }

    /// <summary>Identifies the geometry, so a solver built for it can be reused.</summary>
    public string Signature { get; }

    /// <summary>
    /// The geometry of <paramref name="state"/>'s stationary nodes. A node with <c>stationary: false</c> (a car, a
    /// node carried around) may have a point configured, but it is only where the node sometimes is: every distance
    /// to it would be wrong whenever it is elsewhere, so it has no place in the geometry, as in node-to-node links.
    /// </summary>
    public static DeviceGeometry From(State state)
    {
        var nodes = state.Nodes.Values
            .Where(n => n is { HasLocation: true, Stationary: true })
            .ToDictionary(n => n.Id, n => n.Location, StringComparer.OrdinalIgnoreCase);
        var floors = state.Floors.Values
            .Where(f => f.Bounds is { Length: >= 2 })
            .Select(f => (Min: f.Bounds![0], Max: f.Bounds[1]))
            .ToList();
        return new DeviceGeometry(nodes, floors);
    }
}
