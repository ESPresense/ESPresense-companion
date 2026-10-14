using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

/// <summary>
/// The fingerprint survey smoothed into a continuous surface: each node's raw rssi kernel-regressed
/// over position on a lattice, so a device between surveyed spots is matched against what the radio
/// would read there instead of snapping to the nearest few spots.
///
/// A node a fingerprint did not hear counts as <c>missingRssi</c> there. Lattice points with little
/// survey support nearby (kernel mass below <see cref="MinMass"/>) are left out, so the surface never
/// extrapolates far beyond the survey.
/// </summary>
public sealed class FingerprintSurface
{
    private const double MinMass = 0.05;

    private readonly string[] _nodes;
    private readonly Point3D[] _points;
    private readonly double[][] _predicted; // lattice point x node
    private readonly double _missing;

    public int Count => _points.Length;

    public FingerprintSurface(IReadOnlyList<Fingerprint> fingerprints, double bandwidth, double missingRssi, double step = 0.25)
    {
        _missing = missingRssi;
        _nodes = fingerprints.SelectMany(f => f.Rssi.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (fingerprints.Count == 0)
        {
            _points = Array.Empty<Point3D>();
            _predicted = Array.Empty<double[]>();
            return;
        }

        var values = fingerprints.Select(f => _nodes.Select(n => f.Rssi.TryGetValue(n, out var v) ? v : missingRssi).ToArray()).ToArray();
        double x0 = fingerprints.Min(f => f.X) - 1, x1 = fingerprints.Max(f => f.X) + 1;
        double y0 = fingerprints.Min(f => f.Y) - 1, y1 = fingerprints.Max(f => f.Y) + 1;
        var twoBw2 = 2 * bandwidth * bandwidth;

        var points = new List<Point3D>();
        var predicted = new List<double[]>();
        for (var x = x0; x <= x1; x += step)
            for (var y = y0; y <= y1; y += step)
            {
                double mass = 0, z = 0;
                var sum = new double[_nodes.Length];
                for (var j = 0; j < fingerprints.Count; j++)
                {
                    var f = fingerprints[j];
                    var w = Math.Exp(-((x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y)) / twoBw2);
                    if (w < 1e-9) continue;
                    mass += w;
                    z += w * f.Z;
                    for (var n = 0; n < _nodes.Length; n++) sum[n] += w * values[j][n];
                }
                if (mass < MinMass) continue;
                for (var n = 0; n < _nodes.Length; n++) sum[n] /= mass;
                points.Add(new Point3D(x, y, z / mass));
                predicted.Add(sum);
            }
        _points = points.ToArray();
        _predicted = predicted.ToArray();
    }

    /// <summary>
    /// The 1/(d + 0.5)-weighted centroid of the <paramref name="best"/> lattice points whose predicted
    /// rssi best matches <paramref name="reading"/>. The match is offset-free: the mean dB difference,
    /// estimated over nodes the reading heard and the surface expects to be heard, is removed before
    /// taking the sample standard deviation over every node either side knows.
    /// </summary>
    public FingerprintLocator.MatchResult? Match(IReadOnlyDictionary<string, double> reading, int minCommon, int best = 20)
    {
        var known = _nodes.Select(n => reading.ContainsKey(n)).ToArray();
        var shared = known.Count(k => k);
        if (shared < minCommon || _points.Length == 0) return null;

        // Nodes the reading heard that no fingerprint ever did: expected at the floor everywhere.
        var extra = reading.Where(kv => !_nodes.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Value).ToArray();
        var r = _nodes.Select(n => reading.TryGetValue(n, out var v) ? v : _missing).ToArray();

        var scored = new (double D, int I)[_points.Length];
        var n = r.Length + extra.Length;
        for (var i = 0; i < _points.Length; i++)
        {
            var p = _predicted[i];
            double offSum = 0; var offN = 0;
            for (var k = 0; k < r.Length; k++)
                if (known[k] && p[k] > _missing + 1) { offSum += r[k] - p[k]; offN++; }
            foreach (var e in extra) { offSum += e - _missing; offN++; }
            var off = offN > 0 ? offSum / offN : 0;
            double ss = 0;
            for (var k = 0; k < r.Length; k++) { var d = r[k] - p[k] - off; ss += d * d; }
            foreach (var e in extra) { var d = e - _missing - off; ss += d * d; }
            scored[i] = (Math.Sqrt(ss / Math.Max(1, n - 1)), i);
        }

        var top = scored.OrderBy(s => s.D).Take(best).ToArray();
        var w = top.Select(t => 1 / (t.D + 0.5)).ToArray();
        var ws = w.Sum();
        var loc = new Point3D(
            top.Select((t, i) => _points[t.I].X * w[i]).Sum() / ws,
            top.Select((t, i) => _points[t.I].Y * w[i]).Sum() / ws,
            top.Select((t, i) => _points[t.I].Z * w[i]).Sum() / ws);
        return new FingerprintLocator.MatchResult(loc, top[0].D, shared);
    }
}
