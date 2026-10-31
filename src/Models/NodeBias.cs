using MathNet.Numerics.Statistics;

namespace ESPresense.Models;

/// <summary>
/// A node whose links read consistently long or short compared with the rest of the network: the
/// signature of a node in the wrong place on the map, or at the wrong height.
/// </summary>
/// <param name="Percent">Median link error relative to the network's: 0.4 reads 40% long</param>
public readonly record struct NodeBias(string Id, string? Name, double Percent, int Links)
{
    private const double Threshold = 0.25;
    private const double Agreement = 0.75;
    private const int MinLinks = 3;

    public string Hint =>
        $"{Name ?? Id} reads {Math.Abs(Percent):P0} {(Percent > 0 ? "long" : "short")} against its {Links} neighbours. Check its placement or height.";

    public static IReadOnlyList<NodeBias> Find(OptimizationSnapshot snapshot)
    {
        var links = snapshot.Measures
            .Select(m => (m, map: m.Rx.Location.DistanceTo(m.Tx.Location)))
            .Where(l => l.map > 0 && l.m.Distance > 0)
            .Select(l => (l.m, error: (l.m.Distance - l.map) / l.map))
            .ToArray();
        if (links.Length == 0) return [];

        // A network that reads long everywhere is a calibration problem, not one node's
        var network = links.Select(l => l.error).Median();

        return links
            .SelectMany(l => new[] { (node: l.m.Rx, l.error), (node: l.m.Tx, l.error) })
            .GroupBy(l => l.node.Id)
            .Select(g =>
            {
                var errors = g.Select(l => l.error - network).ToArray();
                var bias = errors.Median();
                var agreeing = errors.Count(e => Math.Sign(e) == Math.Sign(bias));
                return (node: g.First().node, bias, count: errors.Length, agreeing);
            })
            .Where(n => n.count >= MinLinks && Math.Abs(n.bias) >= Threshold && n.agreeing >= Agreement * n.count)
            .OrderByDescending(n => Math.Abs(n.bias))
            .Select(n => new NodeBias(n.node.Id, n.node.Name, n.bias, n.count))
            .ToArray();
    }
}
