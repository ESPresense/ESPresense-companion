using ESPresense.Extensions;
using ESPresense.Locators;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public readonly record struct ExplainedPoint(double X, double Y, double Z)
{
    public static ExplainedPoint? From(Point3D? p) => p is { } v ? new ExplainedPoint(v.X, v.Y, v.Z) : null;
}

public readonly record struct ExplainedPlace(string? Id, string? Name);

public readonly record struct ExplainedReading(
    string? Id, string? Name, bool Current,
    double Rssi, double? RssiVar, double RefRssi,
    double Distance, double? DistVar, DateTime? LastHit);

/// <param name="Reason">Why an unused node was left out: Stale, NotOnFloor, OverMaxDistance, NotNearest or NotUsed</param>
/// <param name="Measured">The distance the locator used</param>
/// <param name="Fitted">Distance from the fit to the node, after the fit's scale</param>
/// <param name="Residual">Measured minus fitted: positive means the node reads long</param>
public readonly record struct ExplainedNode(
    string? Id, string? Name, bool Used, string? Reason,
    double? Measured, double? Fitted, double? Residual, double? Weight);

public readonly record struct ExplainedScenario(
    string Name, string Locator, ExplainedPlace? Floor, ExplainedPlace? Room,
    bool Best, bool Current,
    double Probability, int? Confidence, double WeightedConfidence,
    LocatorOutcome? Outcome, string? OutcomeReason,
    ExplainedPoint Location, ExplainedPoint? Fit,
    double? Scale, double? Error, double? PearsonCorrelation, int? Iterations, string ReasonForExit, int? Fixes,
    IReadOnlyList<ExplainedNode> Nodes);

/// <summary>
/// One standard deviation of the fit's horizontal position, from the geometry of the nodes it used
/// and how well they agreed. Long and thin means the nodes pin one direction and not the other.
/// </summary>
/// <param name="AngleDegrees">Direction of the major axis, counter-clockwise from the map's +x</param>
public readonly record struct ExplainedEllipse(ExplainedPoint Center, double SemiMajor, double SemiMinor, double AngleDegrees);

public readonly record struct ExplainedFloor(ExplainedPlace Floor, double Probability, int? Confidence);

/// <param name="Inside">Whether the best scenario's position is inside Room's polygon right now;
/// a position that stops moving keeps its room, so this can be false while Room is set</param>
/// <param name="DistanceToEdge">Distance from that position to Room's nearest wall</param>
/// <param name="NearestOtherRoom">The next room over: where a drift across that wall would land</param>
/// <param name="PreviousState">What was published before the current state</param>
public readonly record struct ExplainedRoom(
    ExplainedPlace? Room, bool Inside, double? DistanceToEdge,
    ExplainedPlace? NearestOtherRoom, double? NearestOtherDistance,
    string? State, string? PreviousState, DateTime? StateChanged);

/// <summary>
/// Why a device is where it is, in the order the decision was made: what was heard, what each
/// locator did with it, which floor won, and how that became a room.
/// </summary>
public sealed record DeviceExplanation(
    string Id, string? Name, bool Anchored,
    ExplainedPoint? Location, int? Confidence,
    IReadOnlyList<ExplainedReading> Nodes,
    IReadOnlyList<ExplainedScenario> Scenarios,
    IReadOnlyList<ExplainedFloor> Floors,
    ExplainedRoom Room,
    ExplainedEllipse? Uncertainty,
    IReadOnlyList<string> Hints)
{
    private const double LargeResidual = 1.5;
    private const double NearWall = 0.5;
    private const double CloseFloorRace = 0.1;

    public static DeviceExplanation From(Device device, IReadOnlyList<NodeBias>? biases = null)
    {
        var links = device.Nodes.Values.Where(dn => dn.Node != null).OrderBy(dn => dn.Distance).ToArray();
        var best = device.BestScenario;

        // Anchored devices skip the locators, so whatever their scenarios hold is not why they're here
        var scenarios = device.IsAnchored
            ? []
            : device.Scenarios.OrderByDescending(s => s.Probability).ThenByDescending(s => s.Confidence).ToArray();

        var floors = scenarios
            .Where(s => s.Current && s.Floor != null)
            .GroupBy(s => s.Floor!)
            .Select(g => new ExplainedFloor(Place(g.Key), g.Sum(s => s.Probability), g.Max(s => s.Confidence)))
            .OrderByDescending(f => f.Probability)
            .ToArray();

        var explained = scenarios.Select(s => ExplainScenario(s, s == best, links)).ToArray();
        var room = RoomDecision(device, best);

        return new DeviceExplanation(
            device.Id, device.Name, device.IsAnchored,
            ExplainedPoint.From(device.Location), device.Confidence,
            links.Select(Reading).ToArray(),
            explained,
            floors,
            room,
            device.IsAnchored ? null : Ellipse(best?.Trace),
            BuildHints(device, links, explained.Where(s => s.Best).Select(s => (ExplainedScenario?)s).FirstOrDefault(), floors, room, biases ?? []));
    }

    private static string[] BuildHints(Device device, DeviceToNode[] links, ExplainedScenario? best, ExplainedFloor[] floors, ExplainedRoom room, IReadOnlyList<NodeBias> biases)
    {
        var hints = new List<string>();
        var heard = links.Count(dn => dn.Current);

        if (device.IsAnchored) hints.Add("Anchored: the position comes from the anchor, so the locators don't run.");
        else if (heard == 0) hints.Add("No node hears this device right now.");
        else if (!device.Track) hints.Add($"Heard by {heard} node(s), but not tracked, so the locators don't run. Add it to devices: in the config to locate it.");
        else if (best == null) hints.Add($"Heard by {heard} node(s), but no locator has produced a position yet.");

        if (best is { } b)
        {
            if (b.OutcomeReason != null) hints.Add($"{b.Name} has no real fit. {b.OutcomeReason}.");

            var worst = b.Nodes.Where(n => n.Residual != null).OrderByDescending(n => Math.Abs(n.Residual!.Value)).Select(n => (ExplainedNode?)n).FirstOrDefault();
            if (worst is { Residual: var r } w && Math.Abs(r!.Value) >= LargeResidual)
                hints.Add($"{w.Name ?? w.Id} disagrees most with this fit: it reads {Math.Abs(r.Value):F1} m {(r > 0 ? "long" : "short")}.");
        }

        if (floors.Length > 1 && floors[0].Probability - floors[1].Probability < CloseFloorRace)
            hints.Add($"The floor race is close: {floors[0].Floor.Name} {floors[0].Probability:P0}, {floors[1].Floor.Name} {floors[1].Probability:P0}.");

        if (!device.IsAnchored)
        {
            if (room.Room == null && room.NearestOtherRoom is { } nearest && best is { Outcome: not LocatorOutcome.NoFix })
                hints.Add($"The position isn't inside any room. The nearest is {nearest.Name} at {room.NearestOtherDistance:F1} m.");
            else if (room.Room is { } current && !room.Inside && best != null)
                hints.Add($"The position has drifted outside {current.Name}, but the room only updates after a move of 10 cm.");
            else if (room is { Inside: true, DistanceToEdge: < NearWall, NearestOtherRoom: { } next, NearestOtherDistance: < 2 * NearWall })
                hints.Add($"Only {room.DistanceToEdge:F1} m from {room.Room?.Name}'s wall, next to {next.Name}, so the room may flip.");
        }

        var used = best?.Nodes.Where(n => n.Used).Select(n => n.Id).ToHashSet() ?? [];
        hints.AddRange(biases.Where(n => used.Contains(n.Id)).Select(n => n.Hint));

        return hints.ToArray();
    }

    private static ExplainedEllipse? Ellipse(LocatorTrace? trace)
    {
        if (trace is not { Fit: { } fit, Used.Length: >= 3 } solved) return null;

        double weightSum = solved.Used.Sum(u => u.Weight ?? 0);
        if (!(weightSum > 0)) return null;

        double xx = 0, xy = 0, yy = 0, squared = 0;
        foreach (var u in solved.Used)
        {
            var range = fit.DistanceTo(u.Location);
            if (range < 1e-6) continue;
            var weight = (u.Weight ?? 0) * solved.Used.Length / weightSum;
            var (ux, uy) = ((fit.X - u.Location.X) / range, (fit.Y - u.Location.Y) / range);
            xx += weight * ux * ux;
            xy += weight * ux * uy;
            yy += weight * uy * uy;
            squared += weight * Math.Pow(u.Measured - range * solved.Scale, 2);
        }

        var det = xx * yy - xy * xy;
        if (!(det > 1e-9)) return null;

        var variance = squared / (solved.Used.Length - 2);
        var (covXx, covXy, covYy) = (variance * yy / det, -variance * xy / det, variance * xx / det);

        var mean = (covXx + covYy) / 2;
        var spread = Math.Sqrt(Math.Pow((covXx - covYy) / 2, 2) + covXy * covXy);
        return new ExplainedEllipse(
            ExplainedPoint.From(fit)!.Value,
            Math.Sqrt(mean + spread),
            Math.Sqrt(Math.Max(0, mean - spread)),
            0.5 * Math.Atan2(2 * covXy, covXx - covYy) * 180 / Math.PI);
    }

    private static ExplainedReading Reading(DeviceToNode dn) => new(
        dn.Node?.Id, dn.Node?.Name, dn.Current,
        dn.Rssi, dn.RssiVar, dn.RefRssi,
        dn.Distance, dn.DistVar, dn.LastHit);

    private static ExplainedScenario ExplainScenario(Scenario s, bool best, DeviceToNode[] links)
    {
        var trace = s.Trace;
        return new ExplainedScenario(
            s.Label, s.LocatorName, PlaceOrNull(s.Floor), Place(s.Room),
            best, s.Current,
            s.Probability, s.Confidence, s.WeightedConfidence,
            trace?.Outcome, OutcomeReason(s, trace),
            ExplainedPoint.From(s.Location)!.Value, ExplainedPoint.From(trace?.Fit),
            trace?.Fit == null ? null : trace.Scale, s.Error, s.PearsonCorrelation, s.Iterations, $"{s.ReasonForExit}", s.Fixes,
            trace == null ? [] : ExplainNodes(trace, s, links));
    }

    private static string? OutcomeReason(Scenario s, LocatorTrace? trace) => trace?.Outcome switch
    {
        LocatorOutcome.NoFix => $"Heard by {trace.Used.Length} usable node(s)",
        LocatorOutcome.Fallback when s.IsNearestNode => "Nearest node, no fit",
        LocatorOutcome.Fallback when trace.Used.Length < 3 => $"Under 3 nodes ({trace.Used.Length}), no fit",
        LocatorOutcome.Fallback => "No fit: floor has no bounds or the solver failed",
        _ => null
    };

    private static ExplainedNode[] ExplainNodes(LocatorTrace trace, Scenario s, DeviceToNode[] links)
    {
        var used = trace.Used.Select(u => Used(u, trace));
        var rejected = links
            .Where(dn => !trace.Used.Any(u => u.Link == dn))
            .Select(dn => new ExplainedNode(dn.Node?.Id, dn.Node?.Name, false, Rejection(dn, trace, s), null, null, null, null));
        return used.Concat(rejected).ToArray();
    }

    private static ExplainedNode Used(LocatorTraceNode u, LocatorTrace trace)
    {
        var fitted = trace.Fit is { } fit ? fit.DistanceTo(u.Location) * trace.Scale : (double?)null;
        return new ExplainedNode(u.Link.Node?.Id, u.Link.Node?.Name, true, null, u.Measured, fitted, u.Measured - fitted, u.Weight);
    }

    private static string Rejection(DeviceToNode dn, LocatorTrace trace, Scenario s)
    {
        if (!dn.Current) return "Stale";
        if (trace.Floor != null && !(dn.Node?.Floors?.Contains(trace.Floor) ?? false)) return "NotOnFloor";
        if (trace.MaxDistance > 0 && dn.Distance > trace.MaxDistance) return "OverMaxDistance";
        return s.IsNearestNode ? "NotNearest" : "NotUsed";
    }

    private static ExplainedRoom RoomDecision(Device device, Scenario? best)
    {
        var room = device.Room;
        var at = device.IsAnchored ? device.Location?.ToPoint2D() : best?.Location.ToPoint2D();
        var floor = device.Floor ?? room?.Floor;

        var inside = at is { } p && (room?.Polygon?.EnclosesPoint(p) ?? false);
        var nearest = at is { } q
            ? floor?.Rooms.Values
                .Where(r => r != room && r.Polygon is not null)
                .Select(r => (room: r, distance: DistanceToEdge(r.Polygon!, q)))
                .MinBy(r => r.distance)
            : null;

        return new ExplainedRoom(
            Place(room), inside,
            at is { } e && room?.Polygon is not null ? DistanceToEdge(room.Polygon, e) : null,
            Place(nearest?.room), nearest?.distance,
            device.ReportedState, device.PreviousReportedState, device.ReportedStateChanged);
    }

    private static double DistanceToEdge(Polygon2D polygon, Point2D point) =>
        polygon.Edges.Select(e => e.ClosestPointTo(point).DistanceTo(point)).DefaultIfEmpty(double.PositiveInfinity).Min();

    private static ExplainedPlace Place(Floor floor) => new(floor.Id, floor.Name);
    private static ExplainedPlace? PlaceOrNull(Floor? floor) => floor == null ? null : Place(floor);
    private static ExplainedPlace? Place(Room? room) => room == null ? null : new ExplainedPlace(room.Id, room.Name);
}
