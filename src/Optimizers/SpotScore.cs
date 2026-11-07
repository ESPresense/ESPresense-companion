using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>A device at a known position and what each receiver heard from it: raw RSSI, with no rxAdj applied.</summary>
internal sealed record KnownPositionSpot(string Id, Point3D Location, IReadOnlyList<SpotReading> Readings);

/// <summary>One receiver's averaged raw RSSI (<c>rssi + rxAdj</c> at capture) of a <see cref="KnownPositionSpot"/>.</summary>
internal readonly record struct SpotReading(string RxId, Point3D RxLocation, double RawRssi);

/// <summary>
/// Scores a calibration on devices at known positions (#1868): for each spot, the dB residual of
/// <c>raw − (L + rxAdj − 10·absorption·log10 d)</c> over the receivers that heard it, with the spot's level <c>L</c>
/// fitted (the mean). Fitting the level drops whatever rssi@1m the device advertises or was configured with, which is
/// often a guess, so this judges only how the receivers are calibrated <em>relative to each other</em>: the part that
/// moves a trilaterated position. Node-to-node links cannot do this: there a receiver's error trades off against the
/// transmitting node's txRef, and on one live install they ranked two calibrations the opposite way to devices at
/// surveyed positions.
/// </summary>
internal static class SpotScore
{
    /// <summary>A spot needs this many receivers: with its level fitted, two leave a single degree of freedom.</summary>
    internal const int MinReceivers = 3;

    /// <summary>
    /// Every anchored device in <paramref name="snapshots"/> as one spot, each receiver's raw RSSI averaged. An anchor
    /// without a usable rssi@1m counts too: the fit has to drop it (<see cref="Measure.IsUnusableAnchor"/>), but the
    /// spot's level is fitted here, so its position is all this needs.
    /// </summary>
    public static List<KnownPositionSpot> FromAnchors(IEnumerable<OptimizationSnapshot> snapshots)
    {
        return snapshots
            .SelectMany(s => s.Measures)
            .Where(m => m.Tx?.IsAnchor == true && m.Rx?.Id != null && m.Tx.Id != null && double.IsFinite(m.Rssi))
            .GroupBy(m => m.Tx.Id, StringComparer.Ordinal)
            .Select(g => new KnownPositionSpot(
                g.Key,
                g.Last().Tx.Location,
                g.GroupBy(m => m.Rx.Id, StringComparer.Ordinal)
                    .Select(r => new SpotReading(r.Key, r.Last().Rx.Location, r.Average(m => m.Rssi + (m.RssiRxAdj ?? 0))))
                    .ToList()))
            .Where(s => s.Readings.Count >= MinReceivers)
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The level-free residual of each spot under a calibration: <paramref name="receiver"/> gives a receiver's rxAdj and
    /// absorption. Readings at a degenerate distance are skipped; spots left with fewer than <see cref="MinReceivers"/> are not scored.
    /// </summary>
    public static Dictionary<string, (double Sse, int Readings)> Evaluate(
        IEnumerable<KnownPositionSpot> spots, Func<string, (double RxAdj, double Absorption)> receiver)
    {
        var bySpot = new Dictionary<string, (double, int)>(StringComparer.Ordinal);
        foreach (var spot in spots)
        {
            var offsets = new List<double>();
            foreach (var r in spot.Readings)
            {
                var d = r.RxLocation.DistanceTo(spot.Location);
                if (!(d > 0) || !double.IsFinite(d)) continue;
                var (rxAdj, absorption) = receiver(r.RxId);
                // raw = L + rxAdj − 10·a·log10(d), so each reading implies L; the spread of those is the error.
                offsets.Add(r.RawRssi - rxAdj + 10 * absorption * Math.Log10(d));
            }
            if (offsets.Count < MinReceivers) continue;
            var level = offsets.Average();
            bySpot[spot.Id] = (offsets.Sum(o => (o - level) * (o - level)), offsets.Count);
        }
        return bySpot;
    }

    /// <summary>
    /// The report for one calibration: RMS over every scored reading, and, against <paramref name="baseline"/>, on how
    /// many spots it is better or worse (by more than <paramref name="tieDb2"/> dB² of the spot's squared error).
    /// </summary>
    public static OptimizerSpotScore Report(Dictionary<string, (double Sse, int Readings)> scored,
        Dictionary<string, (double Sse, int Readings)>? baseline = null, double tieDb2 = 0.01)
    {
        var readings = scored.Values.Sum(v => v.Readings);
        var rms = readings > 0 ? Math.Sqrt(scored.Values.Sum(v => v.Sse) / readings) : double.NaN;
        if (baseline == null) return new OptimizerSpotScore(scored.Count, readings, rms);

        int better = 0, worse = 0;
        foreach (var (id, (sse, _)) in scored)
        {
            if (!baseline.TryGetValue(id, out var b)) continue;
            if (sse < b.Sse - tieDb2) better++;
            else if (sse > b.Sse + tieDb2) worse++;
        }
        return new OptimizerSpotScore(scored.Count, readings, rms) { Better = better, Worse = worse };
    }
}
