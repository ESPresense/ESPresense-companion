using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Locators;

/// <summary>
/// The fingerprint survey smoothed into a continuous surface: each node's raw rssi kernel-regressed
/// over position on a lattice, so a device between surveyed spots is matched against what the radio
/// would read there instead of snapping to the nearest few spots.
///
/// Given node positions, the regression runs on the residual over a log-distance prior rather than on
/// raw rssi. The prior carries the steep part of the field near the node, so the kernel does not smear
/// it, and the surface degrades towards physics rather than towards nothing at the edge of the survey.
/// The prior's constants are not calibration: whatever they get wrong, the residual absorbs, so the
/// surface never depends on what the optimizer currently has the nodes set to. On the robot survey it
/// trimmed the worst errors (p90 1.95 to 1.76 m) and cost the median 0.05 m, so it is opt-in
/// (<c>locators.fingerprint.prior</c>).
///
/// Each node also carries a spread: how far repeat visits to the same spot disagreed about it, pooled
/// over the survey and floored at <see cref="DefaultSigmaFloor"/>. On the robot survey turning the
/// beacon in place swung some nodes by 3 dB and others by 14, so the match and the tracker's likelihood
/// trust a node in proportion to how repeatable it is. The spread is deliberately not the local spatial
/// variation of the surface (a node whose reading changes fast across a metre is the most informative one
/// about position, not the least), and not the surface's leave-one-out interpolation error either: adding
/// that was tried and made both the match and the tracker slightly worse on the robot survey.
///
/// A node a complete fingerprint did not hear counts as <c>missingRssi</c> there. A node a sparse
/// fingerprint (a time bin taken while the surveyor moved) says nothing about contributes nothing for
/// that node. Lattice points with little survey support nearby (kernel mass below <see cref="MinMass"/>)
/// are left out, so the surface never extrapolates far beyond the survey; a node with no support at a
/// point is left out of the comparison there.
/// </summary>
public sealed class FingerprintSurface
{
    public const double DefaultSigmaFloor = 2.0;
    private const double MinMass = 0.05;

    // Shape-only path-loss prior for placed nodes; see the class remarks.
    private const double PriorRef = -59, PriorExponent = 2.5, PriorMinDistance = 0.5;

    private readonly string[] _nodes;
    private readonly Dictionary<string, int> _nodeIndex;
    private readonly Point3D[] _points;
    private readonly double[][] _predicted; // lattice point x node; NaN where the node has no support
    private readonly double[] _sigma;       // per node
    private readonly double _missing;
    private readonly double _step;
    private readonly Dictionary<(int, int), int> _latticeIndex = new();
    private readonly (int Ix, int Iy)[] _cells;

    public int Count => _points.Length;
    public IReadOnlyList<string> Nodes => _nodes;
    public double Step => _step;
    public double MissingRssi => _missing;
    public Point3D Point(int i) => _points[i];
    public int NodeIndex(string node) => _nodeIndex.TryGetValue(node, out var i) ? i : -1;
    /// <summary>Predicted raw rssi of a node at a lattice point; NaN when the survey has nothing on that node there.</summary>
    public double Predicted(int point, int node) => _predicted[point][node];
    /// <summary>Repeatability of a node's reading (dB), the unit the match and the likelihood measure deviations in.</summary>
    public double Sigma(int node) => _sigma[node];

    public FingerprintSurface(IReadOnlyList<Fingerprint> fingerprints, double bandwidth, double missingRssi,
        Func<string, Point3D?>? nodeLocation = null, double step = 0.25, double sigmaFloor = DefaultSigmaFloor)
    {
        _missing = missingRssi;
        _step = step;
        _nodes = fingerprints.SelectMany(f => f.Rssi.Keys.Concat(f.Silent ?? Array.Empty<string>()))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _nodeIndex = _nodes.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i, StringComparer.OrdinalIgnoreCase);
        if (fingerprints.Count == 0)
        {
            _points = Array.Empty<Point3D>();
            _predicted = Array.Empty<double[]>();
            _sigma = Array.Empty<double>();
            _cells = Array.Empty<(int, int)>();
            return;
        }

        var repeat = RepeatSpread(fingerprints, _nodes, sigmaFloor);
        var locations = _nodes.Select(n => nodeLocation?.Invoke(n)).ToArray();

        // Residual of each fingerprint over the prior, per node; NaN where the fingerprint says nothing about the node.
        var residuals = new double[fingerprints.Count][];
        for (var j = 0; j < fingerprints.Count; j++)
        {
            var f = fingerprints[j];
            var row = new double[_nodes.Length];
            var at = new Point3D(f.X, f.Y, f.Z);
            for (var n = 0; n < _nodes.Length; n++)
            {
                double value;
                if (f.Rssi.TryGetValue(_nodes[n], out var v)) value = v;
                else if (f.AbsentMeansSilent || (f.Silent?.Contains(_nodes[n], StringComparer.OrdinalIgnoreCase) ?? false)) value = missingRssi;
                else { row[n] = double.NaN; continue; }
                row[n] = value - Prior(locations[n], at);
            }
            residuals[j] = row;
        }

        double x0 = fingerprints.Min(f => f.X) - 1, x1 = fingerprints.Max(f => f.X) + 1;
        double y0 = fingerprints.Min(f => f.Y) - 1, y1 = fingerprints.Max(f => f.Y) + 1;
        // A non-positive bandwidth makes every kernel weight exp(-d^2/0): NaN on a lattice point that
        // lands exactly on a spot, 0 everywhere else.
        bandwidth = Math.Max(bandwidth, 0.01);
        var twoBw2 = 2 * bandwidth * bandwidth;
        var cutoff = 3 * bandwidth;

        // Fingerprints bucketed by cutoff-sized cells, so each lattice point only visits its neighbourhood.
        var cells = new Dictionary<(int, int), List<int>>();
        for (var j = 0; j < fingerprints.Count; j++)
        {
            var key = ((int)Math.Floor(fingerprints[j].X / cutoff), (int)Math.Floor(fingerprints[j].Y / cutoff));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
            list.Add(j);
        }

        _sigma = new double[_nodes.Length];
        for (var n = 0; n < _nodes.Length; n++) _sigma[n] = Math.Max(sigmaFloor, repeat[n]);

        var points = new List<Point3D>();
        var predicted = new List<double[]>();
        var latticeCells = new List<(int, int)>();
        var massN = new double[_nodes.Length];
        var sumN = new double[_nodes.Length];
        var nx = (int)Math.Floor((x1 - x0) / step);
        var ny = (int)Math.Floor((y1 - y0) / step);
        for (var ix = 0; ix <= nx; ix++)
            for (var iy = 0; iy <= ny; iy++)
            {
                double x = x0 + ix * step, y = y0 + iy * step;
                double mass = 0, z = 0;
                Array.Clear(massN); Array.Clear(sumN);
                var cx = (int)Math.Floor(x / cutoff); var cy = (int)Math.Floor(y / cutoff);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        if (!cells.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;
                        foreach (var j in bucket)
                        {
                            var f = fingerprints[j];
                            var d2 = (x - f.X) * (x - f.X) + (y - f.Y) * (y - f.Y);
                            if (d2 > cutoff * cutoff) continue;
                            var w = Math.Exp(-d2 / twoBw2);
                            if (w < 1e-9) continue;
                            mass += w;
                            z += w * f.Z;
                            var row = residuals[j];
                            for (var n = 0; n < _nodes.Length; n++)
                            {
                                var r = row[n];
                                if (double.IsNaN(r)) continue;
                                massN[n] += w; sumN[n] += w * r;
                            }
                        }
                    }
                if (mass < MinMass) continue;

                var at = new Point3D(x, y, z / mass);
                var pred = new double[_nodes.Length];
                for (var n = 0; n < _nodes.Length; n++)
                    pred[n] = massN[n] < MinMass ? double.NaN : Prior(locations[n], at) + sumN[n] / massN[n];
                _latticeIndex[(ix, iy)] = points.Count;
                latticeCells.Add((ix, iy));
                points.Add(at);
                predicted.Add(pred);
            }
        _points = points.ToArray();
        _predicted = predicted.ToArray();
        _cells = latticeCells.ToArray();
        _x0 = x0; _y0 = y0;
    }

    private readonly double _x0, _y0;

    /// <summary>Spots revisited within this distance count as the same spot for the repeat spread.</summary>
    private const double SameSpot = 0.3;

    /// <summary>
    /// Per-node spread from repeat visits: fingerprints within <see cref="SameSpot"/> of each other are one
    /// spot, and each node's deviations from its mean at that spot are pooled over the survey. Nodes with no
    /// repeat visits, and every node of a single-visit survey, get 0; the caller applies the floor.
    /// </summary>
    internal static double[] RepeatSpread(IReadOnlyList<Fingerprint> fingerprints, string[] nodes, double floor)
    {
        var sigma = new double[nodes.Length];
        var spot = new int[fingerprints.Count];
        Array.Fill(spot, -1);
        var spots = 0;
        for (var j = 0; j < fingerprints.Count; j++)
        {
            if (spot[j] >= 0) continue;
            spot[j] = spots;
            for (var k = j + 1; k < fingerprints.Count; k++)
                if (spot[k] < 0 && Math.Abs(fingerprints[j].X - fingerprints[k].X) <= SameSpot && Math.Abs(fingerprints[j].Y - fingerprints[k].Y) <= SameSpot &&
                    Math.Sqrt(Math.Pow(fingerprints[j].X - fingerprints[k].X, 2) + Math.Pow(fingerprints[j].Y - fingerprints[k].Y, 2)) <= SameSpot)
                    spot[k] = spots;
            spots++;
        }
        var bySpot = Enumerable.Range(0, fingerprints.Count).GroupBy(j => spot[j]).Where(g => g.Count() > 1).ToArray();
        for (var n = 0; n < nodes.Length; n++)
        {
            double ss = 0; var dof = 0;
            foreach (var g in bySpot)
            {
                var values = g.Select(j => fingerprints[j].Rssi.TryGetValue(nodes[n], out var v) ? v : double.NaN).Where(double.IsFinite).ToArray();
                if (values.Length < 2) continue;
                var mean = values.Average();
                ss += values.Sum(v => (v - mean) * (v - mean));
                dof += values.Length - 1;
            }
            if (dof > 0) sigma[n] = Math.Sqrt(ss / dof);
        }
        return sigma;
    }

    private static double Prior(Point3D? node, Point3D at) =>
        node is { } n ? PriorRef - 10 * PriorExponent * Math.Log10(Math.Max(PriorMinDistance, n.DistanceTo(at))) : 0;

    /// <summary>Lattice column and row of a point, for walking its neighbours.</summary>
    public (int Ix, int Iy) Cell(int i) => _cells[i];
    /// <summary>The lattice point at a column and row, or -1 where the survey has no support.</summary>
    public int At(int ix, int iy) => _latticeIndex.TryGetValue((ix, iy), out var i) ? i : -1;

    /// <summary>The lattice point nearest to (x, y) within <paramref name="maxDistance"/>, or -1.</summary>
    public int Nearest(double x, double y, double maxDistance)
    {
        if (_points.Length == 0) return -1;
        var ix = (int)Math.Round((x - _x0) / _step);
        var iy = (int)Math.Round((y - _y0) / _step);
        var reach = (int)Math.Ceiling(maxDistance / _step);
        var best = -1; var bestD2 = maxDistance * maxDistance;
        for (var dx = -reach; dx <= reach; dx++)
            for (var dy = -reach; dy <= reach; dy++)
            {
                if (!_latticeIndex.TryGetValue((ix + dx, iy + dy), out var i)) continue;
                var p = _points[i];
                var d2 = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d2 <= bestD2) { bestD2 = d2; best = i; }
            }
        return best;
    }

    /// <summary>A device's reading aligned to this surface's node order, so scoring many lattice points does no lookups.</summary>
    public sealed class Reading
    {
        internal readonly double[] Values;
        internal readonly bool[] Has;
        /// <summary>Nodes the reading heard that this surface knows.</summary>
        public int Shared { get; }

        internal Reading(FingerprintSurface s, IReadOnlyDictionary<string, double> reading)
        {
            Values = new double[s._nodes.Length];
            Has = new bool[s._nodes.Length];
            var shared = 0;
            for (var k = 0; k < s._nodes.Length; k++)
                if (reading.TryGetValue(s._nodes[k], out var v)) { Values[k] = v; Has[k] = true; shared++; }
            Shared = shared;
        }
    }

    public Reading Prepare(IReadOnlyDictionary<string, double> reading) => new(this, reading);

    /// <summary>
    /// The device's dB offset from the survey beacon as seen at lattice point <paramref name="i"/>: the
    /// 1/σ²-weighted mean difference over nodes the reading heard and the surface expects to be heard.
    /// NaN when no node qualifies.
    /// </summary>
    public double Offset(int i, Reading r)
    {
        var p = _predicted[i]; var s = _sigma;
        double sum = 0, wsum = 0;
        for (var k = 0; k < p.Length; k++)
        {
            if (!r.Has[k] || double.IsNaN(p[k]) || p[k] <= _missing + 1) continue;
            var w = 1 / (s[k] * s[k]);
            sum += w * (r.Values[k] - p[k]); wsum += w;
        }
        return wsum > 0 ? sum / wsum : double.NaN;
    }

    /// <summary>
    /// How badly the reading fits lattice point <paramref name="i"/> with the device offset removed. A node the
    /// reading heard is compared to its prediction; a node it did not hear, where the surface expects it to be
    /// heard, is compared as <see cref="MissingRssi"/>; a node silent on both sides agrees. Nodes without
    /// support at the point are skipped. Returns the mean squared z (deviation over the node's spread),
    /// the plain rms deviation in dB, and how many nodes took part.
    /// </summary>
    public (double Chi2, double RmsDb, int Used) Evaluate(int i, Reading r, double? offset = null)
    {
        var p = _predicted[i]; var s = _sigma;
        var off = offset ?? Offset(i, r);
        if (double.IsNaN(off)) off = 0;
        double chi2 = 0, ss = 0; var n = 0;
        for (var k = 0; k < p.Length; k++)
        {
            if (double.IsNaN(p[k])) continue;
            double d;
            if (r.Has[k]) d = r.Values[k] - p[k] - off;
            else if (p[k] > _missing + 1) d = _missing - p[k] - off;
            else continue;
            chi2 += d * d / (s[k] * s[k]); ss += d * d; n++;
        }
        var dof = Math.Max(1, n - 1);
        return (chi2 / dof, Math.Sqrt(ss / dof), n);
    }

    /// <summary>
    /// Log-likelihood of the nodes flagged in <paramref name="include"/> at lattice point <paramref name="i"/>
    /// for a device with the given offset, tempered by <paramref name="temper"/> (fading on neighbouring
    /// reports is correlated, so each one is worth less than an independent sample). Nodes without support
    /// at the point contribute nothing.
    /// </summary>
    public double LogLikelihood(int i, Reading r, bool[] include, double offset, double temper)
    {
        var p = _predicted[i]; var s = _sigma;
        double ll = 0;
        for (var k = 0; k < p.Length; k++)
        {
            if (!include[k] || double.IsNaN(p[k])) continue;
            var d = (r.Has[k] ? r.Values[k] : _missing) - p[k] - offset;
            ll -= 0.5 * temper * d * d / (s[k] * s[k]);
        }
        return ll;
    }

    /// <summary>
    /// Evidence from a node that has gone quiet: at points where the surface expects it to be heard the
    /// silence counts as a reading of <see cref="MissingRssi"/>; where it expects silence, nothing.
    /// </summary>
    public double LogLikelihoodSilent(int i, int node, double offset, double temper)
    {
        var p = _predicted[i][node];
        if (double.IsNaN(p) || p <= _missing + 1) return 0;
        var d = _missing - p - offset;
        var s = _sigma[node];
        return -0.5 * temper * d * d / (s * s);
    }

    /// <summary>
    /// The 1/(d + 0.5)-weighted centroid of the <paramref name="best"/> lattice points whose predicted
    /// rssi best matches <paramref name="reading"/>, ranked by <see cref="Evaluate"/>'s spread-weighted fit.
    /// The reported residual is the rms dB deviation of the best point, for the confidence scale.
    /// </summary>
    public FingerprintLocator.MatchResult? Match(IReadOnlyDictionary<string, double> reading, int minCommon, int best = 20)
    {
        // FingerprintLocator always passes an OrdinalIgnoreCase reading, but Match is public and takes
        // any IReadOnlyDictionary: normalize so a differently-cased caller can't disagree with the
        // case-insensitive node lookups.
        reading = new Dictionary<string, double>(reading, StringComparer.OrdinalIgnoreCase);
        var r = Prepare(reading);
        if (r.Shared < minCommon || _points.Length == 0) return null;

        var scored = new (double Chi2, double Db, int I)[_points.Length];
        for (var i = 0; i < _points.Length; i++)
        {
            var (chi2, db, used) = Evaluate(i, r);
            scored[i] = (used == 0 ? double.PositiveInfinity : chi2, db, i);
        }

        var top = scored.Where(t => double.IsFinite(t.Chi2)).OrderBy(t => t.Chi2).Take(Math.Max(1, best)).ToArray();
        if (top.Length == 0) return null;
        var w = top.Select(t => 1 / (t.Db + 0.5)).ToArray();
        var ws = w.Sum();
        var loc = new Point3D(
            top.Select((t, i) => _points[t.I].X * w[i]).Sum() / ws,
            top.Select((t, i) => _points[t.I].Y * w[i]).Sum() / ws,
            top.Select((t, i) => _points[t.I].Z * w[i]).Sum() / ws);
        var samples = top.Select((t, i) => (_points[t.I], w[i])).ToArray();
        return new FingerprintLocator.MatchResult(loc, top[0].Db, r.Shared, samples);
    }
}
