using ESPresense.Locators;
using ESPresense.Utils;
using MathNet.Numerics.Statistics;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

/// <param name="Error">Horizontal distance between where the node was placed and where its neighbours put it; null if unsolved</param>
/// <param name="LinkError">Median of how far its links are from the map; null with no links to placed neighbours</param>
public readonly record struct NodeSelfTest(string Id, string? Name, double? Error, double? LinkError, int Neighbours);

/// <summary>
/// A leave-one-out self-test: each placed node is hidden and located from what its neighbours measure,
/// through the same locator devices use, and the answer is compared with where it really is. Nodes are
/// the only things in the house with a known position, so this is a real error, not a confidence.
/// <para>
/// Median and P95 say what the locator would get. They don't say who to blame: one misplaced node can
/// throw off a neighbour's test further than its own, so <see cref="Worst"/> is the node with the worst
/// median link instead, which one bad neighbour can't move.
/// </para>
/// </summary>
public sealed record PositionAccuracy(
    DateTime Timestamp,
    int Placed, int Solved, int Unsolved,
    double? Median, double? P95,
    NodeSelfTest? Worst,
    string Guidance,
    IReadOnlyList<NodeSelfTest> Nodes,
    IReadOnlyList<string> Hints)
{
    private const int MinNeighbours = 3;
    private const double Good = 1;
    private const double Usable = 2.5;
    private const double OutlierFactor = 2;
    private const double OutlierLinkError = 1;

    public static PositionAccuracy Measure(State state)
    {
        var measures = state.ReadMeasures();
        var nodes = state.Nodes.Values
            .Where(n => n is { HasLocation: true, Stationary: true, Floors.Length: > 0 })
            .Select(n => Test(n, measures, state))
            .OrderByDescending(t => t.Error ?? double.NegativeInfinity)
            .ToArray();

        var errors = nodes.Where(t => t.Error != null).Select(t => t.Error!.Value).OrderBy(e => e).ToArray();
        var worst = nodes.Where(t => t.Error != null).Select(t => (NodeSelfTest?)t).MaxBy(t => t?.LinkError ?? 0);
        var typicalLink = nodes.Where(t => t.LinkError != null).Select(t => t.LinkError!.Value).Median();
        var median = errors.Length == 0 ? (double?)null : errors.Median();
        var p95 = errors.Length == 0 ? (double?)null : errors[(int)Math.Ceiling(0.95 * errors.Length) - 1];

        return new PositionAccuracy(
            measures.Timestamp,
            nodes.Length, errors.Length, nodes.Length - errors.Length,
            median, p95, worst,
            Guide(median, worst, typicalLink),
            nodes,
            NodeBias.Find(measures).Select(b => b.Hint).ToArray());
    }

    private static string Guide(double? median, NodeSelfTest? worst, double typicalLink)
    {
        if (median is not { } m || worst is not { LinkError: { } w } node)
            return $"Needs nodes that hear at least {MinNeighbours} neighbours on their floor.";
        if (w > Math.Max(OutlierLinkError, OutlierFactor * typicalLink))
            return $"Start with {node.Name ?? node.Id}: its links are {w:F1} m from the map when most nodes are within {typicalLink:F1} m. Check its placement or height.";
        return m switch
        {
            <= Good => "Good: nodes locate each other to within about a metre.",
            <= Usable => "Usable for rooms, though positions near walls will flip.",
            _ => "Distances are off across the board. Let the optimizer run, then check again."
        };
    }

    private static NodeSelfTest Test(Node target, OptimizationSnapshot snapshot, State state)
    {
        var floor = SpatialUtils.FindFloorContaining(target.Location, target.Floors!) ?? target.Floors!.First();

        // Both directions of a link describe the same distance
        var neighbours = snapshot.Measures
            .Select(m => m.Rx.Id == target.Id ? (other: m.Tx.Id, m.Distance) : m.Tx.Id == target.Id ? (other: m.Rx.Id, m.Distance) : default)
            .Where(l => l.other != null && l.Distance > 0)
            .GroupBy(l => l.other)
            .Select(g => (node: state.Nodes.GetValueOrDefault(g.Key), distance: g.Average(l => l.Distance)))
            .Where(n => n.node?.HasLocation == true && (n.node.Floors?.Contains(floor) ?? false))
            .ToArray();

        var linkError = neighbours.Length == 0
            ? (double?)null
            : neighbours.Select(n => Math.Abs(n.distance - target.Location.DistanceTo(n.node!.Location))).Median();

        double? error = null;
        if (neighbours.Length >= MinNeighbours)
        {
            var hidden = new Device($"selftest:{target.Id}", null, TimeSpan.FromSeconds(30));
            foreach (var (node, distance) in neighbours)
                hidden.Nodes[node!.Id] = new DeviceToNode(hidden, node) { Distance = distance, LastHit = DateTime.UtcNow };

            var scenario = new Scenario(state.Config, new NelderMeadMultilateralizer(hidden, floor, state), floor.Name);
            scenario.Locate();
            if (scenario.Trace?.Fit is { } fit)
                error = new Point2D(fit.X, fit.Y).DistanceTo(new Point2D(target.Location.X, target.Location.Y));
        }

        return new NodeSelfTest(target.Id, target.Name, error, linkError, neighbours.Length);
    }
}
