using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

/// <summary>
/// Locates a device by matching the RSSI it produces at each node against a survey of known
/// positions (a fingerprint map), instead of turning RSSI into distances and trilaterating.
///
/// Ranging assumes a log-distance model that walls, furniture and antenna patterns break; a
/// fingerprint records what the radio actually does at each spot. On a robot survey this beat every
/// ranging locator (see RobotSurveyTests).
///
/// Matching is offset-free: the mean dB difference between the device's readings and a fingerprint
/// is removed before comparing, so a phone that transmits hotter or colder than the survey beacon
/// still matches. Readings are compared raw (rssi + rxAdj) so a node's rxAdj changing after the
/// survey does not move it.
/// </summary>
public class FingerprintLocator(Device device, Floor floor, FingerprintMap map, FingerprintConfig config) : ILocate
{
    public bool Locate(Scenario scenario)
    {
        var heard = device.Nodes.Values
            .Where(n => n.Current && (n.Node?.Floors?.Contains(floor) ?? false))
            .ToArray();

        if (heard.Length < config.MinCommon)
        {
            scenario.Confidence = 0;
            scenario.Room = null;
            scenario.Error = null;
            return false;
        }

        scenario.Minimum = heard.Min(n => (double?)n.Distance);
        scenario.LastHit = heard.Max(n => n.LastHit);
        scenario.Fixes = heard.Length;
        scenario.Floor = floor;

        try
        {
            var reading = heard.ToDictionary(n => n.Node.Id, n => n.Rssi + (n.RssiRxAdj ?? 0), StringComparer.OrdinalIgnoreCase);
            var match = Match(reading, map.ForFloor(floor.Id), config.K, config.MinCommon);
            if (match == null)
            {
                scenario.Confidence = 0;
                return false;
            }

            scenario.UpdateLocation(match.Location);
            scenario.Error = match.Residual;
            scenario.PearsonCorrelation = null;
            // A 0 dB residual is a perfect match; the repeatability floor on a fixed mount is ~1 dB
            // and a different spot a few metres away typically sits 6+ dB off.
            scenario.Confidence = (int)Math.Clamp(100 - 10 * match.Residual, 1, 100);
        }
        catch (Exception ex)
        {
            scenario.Confidence = 0;
            scenario.Error = null;
            Log.Error("Fingerprint locator error for {Device}: {Message}", device, ex.Message);
        }

        if (scenario.Confidence <= 0) return false;
        if (scenario.Location.DistanceTo(scenario.LastLocation) < 0.1) return false;

        scenario.Room = floor.Rooms.Values.FirstOrDefault(r =>
            r.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        return true;
    }

    public sealed record MatchResult(Point3D Location, double Residual, int Common);

    /// <summary>
    /// Weighted mean position of the <paramref name="k"/> fingerprints closest to
    /// <paramref name="reading"/> (node id → raw rssi). Distance between a reading and a fingerprint is
    /// the sample standard deviation of their per-node dB differences (the mean difference removed),
    /// over the nodes both contain; fingerprints sharing fewer than <paramref name="minCommon"/>
    /// nodes are skipped.
    /// </summary>
    public static MatchResult? Match(IReadOnlyDictionary<string, double> reading, IEnumerable<Fingerprint> fingerprints, int k, int minCommon)
    {
        var scored = new List<(Fingerprint F, double D, int Common)>();
        foreach (var f in fingerprints)
        {
            var deltas = new List<double>();
            foreach (var (node, rssi) in reading)
                if (f.Rssi.TryGetValue(node, out var fr))
                    deltas.Add(rssi - fr);
            if (deltas.Count < minCommon) continue;
            // Removing the mean costs a degree of freedom: divide by n - 1, or fingerprints sharing
            // few nodes would score systematically closer than ones sharing many.
            var mean = deltas.Average();
            var d = Math.Sqrt(deltas.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, deltas.Count - 1));
            scored.Add((f, d, deltas.Count));
        }
        if (scored.Count == 0) return null;

        var best = scored.OrderBy(s => s.D).Take(Math.Max(1, k)).ToArray();
        var w = best.Select(b => 1 / (b.D + 1)).ToArray();
        var ws = w.Sum();
        var loc = new Point3D(
            best.Select((b, i) => b.F.X * w[i]).Sum() / ws,
            best.Select((b, i) => b.F.Y * w[i]).Sum() / ws,
            best.Select((b, i) => b.F.Z * w[i]).Sum() / ws);
        return new MatchResult(loc, best[0].D, best[0].Common);
    }
}
