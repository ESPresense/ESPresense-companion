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
/// fingerprint records what the radio actually does at each spot. On a robot survey with spots
/// 0.75 m apart this located held-out spots to ~1 m median, against ~2 m for the best ranging
/// locator (see RobotSurveyTests). It needs the survey to cover where devices go, and a reading
/// averaged over tens of seconds: ~30 s matched the full survey dwell, 10 s cost ~0.25 m.
///
/// Matching is offset-free: the mean dB difference between the device's readings and a fingerprint
/// is removed before comparing, so a phone that transmits hotter or colder than the survey beacon
/// still matches. Readings are compared raw (rssi + rxAdj) so a node's rxAdj changing after the
/// survey does not move it.
///
/// The room is predicted from the matched spots themselves (a weighted vote of the rooms containing
/// them), so a fix can report a room even when the averaged coordinate lands in a doorway, near
/// a wall or in a gap between room polygons. When no room matches, the floor is reported.
/// </summary>
public class FingerprintLocator(Device device, Floor floor, FingerprintMap map, FingerprintConfig config) : ILocate
{
    public bool Locate(Scenario scenario)
    {
        // Fingerprints need no node coordinates, so every node the survey recorded is a feature, even
        // one on another floor or never placed on the map; the floor only picks which fingerprints to
        // match. Nodes the map has never seen carry no information and are left out.
        var known = map.NodesForFloor(floor.Id);
        var heard = device.Nodes.Values
            .Where(n => n.Current && n.Node != null && known.Contains(n.Node.Id))
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

        MatchResult? match = null;
        try
        {
            var reading = heard.ToDictionary(n => n.Node.Id, n => n.Rssi + (n.RssiRxAdj ?? 0), StringComparer.OrdinalIgnoreCase);
            match = config.Mode == "knn"
                ? Match(reading, map.ForFloor(floor.Id), config.K, config.MinCommon, config.MissingRssi)
                : map.Surface(floor.Id, config.Bandwidth, config.MissingRssi ?? -100).Match(reading, config.MinCommon, config.Top);
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

        if (scenario.Confidence <= 0 || match == null) return false;

        // The room comes from the fingerprints that matched, not just from wherever the averaged
        // coordinate landed: a match near a wall, in a doorway or in a gap between room polygons
        // still belongs to a room, and the matched spots vote for it by the same weight as the fix.
        scenario.Room = RoomFromMatch(match, floor);
        if (scenario.Location.DistanceTo(scenario.LastLocation) < 0.1) return false;
        return true;
    }

    /// <summary>
    /// The room the match points to: the weighted vote of the rooms containing the best-matching
    /// spots. When none of the matched spots sits inside a room polygon the located coordinate is
    /// used as a last resort. If neither matches a room, the floor is reported.
    /// </summary>
    private static Room? RoomFromMatch(MatchResult match, Floor floor)
    {
        var votes = new Dictionary<Room, double>();
        foreach (var (position, weight) in match.Samples)
        {
            var room = floor.Rooms.Values.FirstOrDefault(r =>
                r.Polygon?.EnclosesPoint(position.ToPoint2D()) ?? false);
            if (room != null) votes[room] = votes.GetValueOrDefault(room) + weight;
        }
        if (votes.Count > 0) return votes.OrderByDescending(v => v.Value).First().Key;
        return floor.Rooms.Values.FirstOrDefault(r =>
            r.Polygon?.EnclosesPoint(match.Location.ToPoint2D()) ?? false);
    }

    public sealed record MatchResult(Point3D Location, double Residual, int Common,
        IReadOnlyList<(Point3D Position, double Weight)> Samples);

    /// <summary>
    /// Weighted mean position of the <paramref name="k"/> fingerprints closest to
    /// <paramref name="reading"/> (node id → raw rssi). Distance between a reading and a fingerprint is
    /// the sample standard deviation of their per-node dB differences (the mean difference removed).
    /// A node heard on one side only means "far from that node": with <paramref name="missingRssi"/> set
    /// it stands in for the silent side, otherwise only shared nodes are compared. Fingerprints sharing
    /// fewer than <paramref name="minCommon"/> nodes are skipped.
    /// </summary>
    public static MatchResult? Match(IReadOnlyDictionary<string, double> reading, IEnumerable<Fingerprint> fingerprints, int k, int minCommon, double? missingRssi = null)
    {
        var scored = new List<(Fingerprint F, double D, int Common)>();
        foreach (var f in fingerprints)
        {
            var deltas = new List<double>();
            var common = 0;
            foreach (var (node, rssi) in reading)
                if (f.Rssi.TryGetValue(node, out var fr)) { deltas.Add(rssi - fr); common++; }
            if (common < minCommon) continue;
            // The device's offset from the survey beacon is estimated on shared nodes only: a
            // stand-in missing value carries no offset, and would bias it.
            var mean = deltas.Average();
            if (missingRssi is { } miss)
            {
                foreach (var (node, rssi) in reading)
                    if (!f.Rssi.ContainsKey(node)) deltas.Add(rssi - miss);
                foreach (var (node, fr) in f.Rssi)
                    if (!reading.ContainsKey(node)) deltas.Add(miss - fr);
            }
            // Removing the offset costs a degree of freedom: divide by n - 1, or fingerprints sharing
            // few nodes would score systematically closer than ones sharing many.
            var d = Math.Sqrt(deltas.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, deltas.Count - 1));
            scored.Add((f, d, common));
        }
        if (scored.Count == 0) return null;

        var best = scored.OrderBy(s => s.D).Take(Math.Max(1, k)).ToArray();
        var w = best.Select(b => 1 / (b.D + 1)).ToArray();
        var ws = w.Sum();
        var loc = new Point3D(
            best.Select((b, i) => b.F.X * w[i]).Sum() / ws,
            best.Select((b, i) => b.F.Y * w[i]).Sum() / ws,
            best.Select((b, i) => b.F.Z * w[i]).Sum() / ws);
        var samples = best.Select((b, i) => (new Point3D(b.F.X, b.F.Y, b.F.Z), w[i])).ToArray();
        return new MatchResult(loc, best[0].D, best[0].Common, samples);
    }
}
