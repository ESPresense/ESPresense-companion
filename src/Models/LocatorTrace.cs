using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public enum LocatorOutcome
{
    /// <summary>The locator ran its fit over the used nodes</summary>
    Solved,
    /// <summary>A position was produced without a fit (too few nodes, no floor bounds, solver failure, nearest node)</summary>
    Fallback,
    /// <summary>Not enough usable nodes to produce a position</summary>
    NoFix
}

/// <summary>
/// What one locator pass consumed and produced, snapshotted so a reader sees the distances the
/// locator actually used rather than whatever has arrived since.
/// </summary>
public sealed record LocatorTrace(
    LocatorOutcome Outcome,
    Floor? Floor,
    LocatorTraceNode[] Used,
    Point3D? Fit = null,
    double Scale = 1,
    double? MaxDistance = null)
{
    public static LocatorTrace NoFix(Floor? floor, IEnumerable<DeviceToNode> nodes) =>
        new(LocatorOutcome.NoFix, floor, Snapshot(nodes, null));

    /// <param name="solvedWeights">The fit's per-node weights; null when the position came from a fallback</param>
    /// <param name="fit">The unfiltered position the fit landed on</param>
    /// <param name="scale">The factor the fit applied to geometric distance; 1 if it didn't apply one</param>
    public static LocatorTrace Finished(Floor? floor, IEnumerable<DeviceToNode> nodes, double[]? solvedWeights, Point3D fit, double scale = 1) =>
        solvedWeights == null
            ? new(LocatorOutcome.Fallback, floor, Snapshot(nodes, null))
            : new(LocatorOutcome.Solved, floor, Snapshot(nodes, solvedWeights), fit, scale);

    private static LocatorTraceNode[] Snapshot(IEnumerable<DeviceToNode> nodes, double[]? weights) =>
        nodes.Select((dn, i) => new LocatorTraceNode(dn, dn.Node.Location, dn.Distance, weights?[i])).ToArray();
}

public readonly record struct LocatorTraceNode(DeviceToNode Link, Point3D Location, double Measured, double? Weight);
