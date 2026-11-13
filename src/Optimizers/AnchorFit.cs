using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Optimizers;

/// <summary>An anchor at one position and what each receiver heard from it: raw RSSI, with no rxAdj applied.</summary>
internal sealed record AnchorReadings(string Id, Point3D Location, IReadOnlyList<AnchorReading> Readings);

/// <summary>One receiver's averaged raw RSSI (<c>rssi + rxAdj</c> at capture) of an anchor.</summary>
internal readonly record struct AnchorReading(string RxId, Point3D RxLocation, double RawRssi);

/// <summary>
/// How well a calibration fits the anchors (#1868): for each anchor, the dB residual of
/// <c>raw − (L + rxAdj − 10·absorption·log10 d)</c> over the receivers that heard it, with the anchor's level <c>L</c>
/// fitted (the mean). Fitting the level drops the anchor's rssi@1m, which is often a guess, so this judges only how the
/// receivers are calibrated <em>relative to each other</em>: the part that moves a trilaterated position. Node-to-node
/// links cannot do this: there a receiver's error trades off against the transmitting node's txRef, and on one live
/// install they ranked two calibrations the opposite way to a device at surveyed positions.
/// </summary>
internal static class AnchorFit
{
    /// <summary>An anchor needs this many receivers: with its level fitted, two leave a single degree of freedom.</summary>
    internal const int MinReceivers = 3;

    /// <summary>
    /// Every anchor in <paramref name="snapshots"/>, each receiver's raw RSSI averaged. An anchor without a usable
    /// rssi@1m counts too: the fit has to drop it (<see cref="Measure.IsUnusableAnchor"/>), but the level is fitted
    /// here, so its position is all this needs. Readings without the rxAdj they were taken with are skipped.
    /// </summary>
    public static List<AnchorReadings> FromSnapshots(IEnumerable<OptimizationSnapshot> snapshots)
    {
        return snapshots
            .SelectMany(s => s.Measures)
            // Without the rxAdj a reading was taken with, its raw RSSI is unknown: re-basing it would turn that
            // receiver's applied adjustment into an apparent calibration error. Leave it out, as the fit does.
            .Where(m => m.Tx?.IsAnchor == true && m.Rx?.Id != null && m.Tx.Id != null && double.IsFinite(m.Rssi) && m.RssiRxAdj != null)
            // One entry per anchor per position: an anchor moved during the window is scored at each position,
            // not at its last position with readings from both.
            .GroupBy(m => (m.Tx.Id, m.Tx.Location))
            .Select(g => new AnchorReadings(
                $"{g.Key.Id}@{g.Key.Location.X:0.00},{g.Key.Location.Y:0.00},{g.Key.Location.Z:0.00}",
                g.Key.Location,
                g.GroupBy(m => m.Rx.Id, StringComparer.Ordinal)
                    .Select(r => new AnchorReading(r.Key, r.Last().Rx.Location, r.Average(m => m.Rssi + m.RssiRxAdj!.Value)))
                    .ToList()))
            .Where(a => a.Readings.Count >= MinReceivers)
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The level-free residual of each anchor under a calibration: <paramref name="receiver"/> gives a receiver's rxAdj
    /// and absorption. Readings at a degenerate distance are skipped; anchors left with fewer than
    /// <see cref="MinReceivers"/> are not scored.
    /// </summary>
    public static Dictionary<string, (double Sse, int Readings)> Evaluate(
        IEnumerable<AnchorReadings> anchors, Func<string, (double RxAdj, double Absorption)> receiver)
    {
        var byAnchor = new Dictionary<string, (double, int)>(StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            var offsets = new List<double>();
            foreach (var r in anchor.Readings)
            {
                var d = r.RxLocation.DistanceTo(anchor.Location);
                if (!(d > 0) || !double.IsFinite(d)) continue;
                var (rxAdj, absorption) = receiver(r.RxId);
                // raw = L + rxAdj − 10·a·log10(d), so each reading implies L; the spread of those is the error.
                offsets.Add(r.RawRssi - rxAdj + 10 * absorption * Math.Log10(d));
            }
            if (offsets.Count < MinReceivers) continue;
            var level = offsets.Average();
            byAnchor[anchor.Id] = (offsets.Sum(o => (o - level) * (o - level)), offsets.Count);
        }
        return byAnchor;
    }

    /// <summary>
    /// The report for one calibration: RMS over every scored reading, and, against <paramref name="baseline"/>, on how
    /// many anchors it is better or worse (by more than <paramref name="tieDb2"/> dB² of the anchor's squared error).
    /// </summary>
    public static OptimizerAnchorFit Report(Dictionary<string, (double Sse, int Readings)> scored,
        Dictionary<string, (double Sse, int Readings)>? baseline = null, double tieDb2 = 0.01)
    {
        var readings = scored.Values.Sum(v => v.Readings);
        var rms = readings > 0 ? Math.Sqrt(scored.Values.Sum(v => v.Sse) / readings) : double.NaN;
        if (baseline == null) return new OptimizerAnchorFit(scored.Count, readings, rms);

        int better = 0, worse = 0;
        foreach (var (id, (sse, _)) in scored)
        {
            if (!baseline.TryGetValue(id, out var b)) continue;
            if (sse < b.Sse - tieDb2) better++;
            else if (sse > b.Sse + tieDb2) worse++;
        }
        return new OptimizerAnchorFit(scored.Count, readings, rms) { Better = better, Worse = worse };
    }
}
