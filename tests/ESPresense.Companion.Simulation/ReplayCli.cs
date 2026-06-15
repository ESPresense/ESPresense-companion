using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Simulation.Tests;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Simulation;

/// <summary>
/// Replays a device capture (raw rssi + ground-truth position trail) through the production
/// locators and scores position error against the ground truth.
///
/// The whole point: record ONCE, evaluate MANY calibrations offline. The firmware turns rssi into
/// distance with  distance = 10^((refRssi - rssi) / (10 * absorption))  — verified against real
/// captures — so the captured rssi is calibration-independent. We recompute distance under any
/// candidate (absorption, refRssi offset) and re-locate, instead of re-recording per change.
///
/// Usage:
///   replay &lt;capture.json&gt; [--locator gaussnewton|neldermead|bfgs|mle|all]
///                         [--absorption N]...   (repeatable; each adds a global-absorption run)
///                         [--ref-delta D]       (dB added to every refRssi for the swept runs)
///                         [--step S]            (fix cadence in seconds, default 1.0)
///                         [--stale S]           (max age of a node reading to use, default 30)
/// With no --absorption, only the "as-captured" baseline is scored.
/// </summary>
public static class ReplayCli
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("Usage: replay <capture.json> [--locator X] [--absorption N]... [--ref-delta D] [--step S] [--stale S]");
            return 2;
        }

        var file = args[0];
        if (!File.Exists(file))
        {
            stderr.WriteLine($"Capture file not found: {file}");
            return 2;
        }

        string locator = "gaussnewton";
        double refDelta = 0;
        double step = 1.0;
        double stale = 30.0;
        var absorptions = new List<double>();
        string? calibrationFile = null;
        double? ransac = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--locator": locator = args[++i]; break;
                case "--absorption": absorptions.Add(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
                case "--ref-delta": refDelta = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--step": step = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--stale": stale = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--calibration": calibrationFile = args[++i]; break;
                // --ransac [threshold]; threshold defaults to 2.0m if the next token is another flag
                case "--ransac":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rt))
                    { ransac = rt; i++; }
                    else ransac = 2.0;
                    break;
            }
        }

        // Optional per-node absorption map (e.g. an optimizer's actual output): {"laundry":3.84,...}
        Dictionary<string, double>? perNode = null;
        if (calibrationFile != null)
        {
            if (!File.Exists(calibrationFile)) { stderr.WriteLine($"Calibration file not found: {calibrationFile}"); return 2; }
            perNode = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(calibrationFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        Capture? cap;
        try
        {
            cap = JsonSerializer.Deserialize<Capture>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"Invalid capture JSON: {ex.Message}");
            return 2;
        }

        if (cap?.Nodes == null || cap.Nodes.Length == 0 || cap.Messages == null || cap.Messages.Length == 0)
        {
            stderr.WriteLine("Capture has no nodes or messages.");
            return 2;
        }
        if (cap.Positions == null || cap.Positions.Length == 0)
        {
            stderr.WriteLine("Capture has no ground-truth positions; cannot score accuracy.");
            return 2;
        }

        var locators = locator.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new[] { "gaussnewton", "neldermead", "bfgs", "mle" }
            : new[] { locator };

        stdout.WriteLine($"Capture: {Path.GetFileName(file)}");
        stdout.WriteLine($"  device={cap.DeviceId}  nodes={cap.Nodes.Length}  messages={cap.Messages.Length}  positions={cap.Positions.Length}");
        stdout.WriteLine($"  step={step}s  stale={stale}s  ref-delta={refDelta}dB");
        stdout.WriteLine();

        var engine = new ReplayEngine(cap, step, stale);

        // Baseline + each requested global absorption.
        var calibrations = new List<(string label, Func<Msg, double> dist)>
        {
            ("as-captured", m => m.Distance)
        };
        foreach (var n in absorptions)
            calibrations.Add(($"abs={n:0.0}{(refDelta != 0 ? $" ref{refDelta:+0;-0}" : "")}",
                m => Recompute(m, n, refDelta)));
        if (perNode != null)
            calibrations.Add(("per-node-file",
                m => perNode.TryGetValue(m.Node, out var n) ? Recompute(m, n, refDelta) : m.Distance));

        // null = plain solve; a threshold = RANSAC consensus solve. Run both when --ransac is set.
        var modes = ransac is double rthr ? new double?[] { null, rthr } : new double?[] { null };

        stdout.WriteLine($"{"locator",-12} {"calibration",-26} {"fixes",6} {"median",8} {"p95",8} {"mean",8}");
        stdout.WriteLine(new string('-', 72));
        foreach (var loc in locators)
        {
            foreach (var (label, distFn) in calibrations)
            {
                foreach (var mode in modes)
                {
                    var lbl = mode is double t ? $"{label} +ransac{t:0.#}" : label;
                    var errors = engine.Score(loc, distFn, mode);
                    if (errors.Count == 0)
                    {
                        stdout.WriteLine($"{loc,-12} {lbl,-26} {0,6}   (no locatable fixes)");
                        continue;
                    }
                    stdout.WriteLine($"{loc,-12} {lbl,-26} {errors.Count,6} {Median(errors),8:0.000} {Percentile(errors, 95),8:0.000} {errors.Average(),8:0.000}");
                }
            }
        }

        // Per-node distance bias (recomputed/captured distance minus true geometric distance),
        // which is what the optimizer is implicitly trying to zero out.
        stdout.WriteLine();
        stdout.WriteLine("Per-node distance bias (as-captured): measured - true geometric");
        stdout.WriteLine($"{"node",-22} {"n",5} {"meanBias",9} {"meanAbs",9} {"impliedAbs",10}");
        stdout.WriteLine(new string('-', 60));
        foreach (var row in engine.PerNodeBias())
            stdout.WriteLine($"{row.Node,-22} {row.Count,5} {row.MeanBias,9:+0.00;-0.00} {row.MeanAbs,9:0.00} {row.ImpliedAbsorption,10:0.00}");

        return 0;
    }

    private static double Recompute(Msg m, double absorption, double refDelta)
        => Math.Max(0.1, Math.Pow(10, ((m.RefRssi + refDelta) - m.Rssi) / (10.0 * absorption)));

    private static double RecomputeAbs(Msg m, double absorption, double refRssi)
        => Math.Max(0.1, Math.Pow(10, (refRssi - m.Rssi) / (10.0 * absorption)));

    /// <summary>
    /// Leave-one-capture-out test of "better calibration is the lever": fit per-node
    /// (absorption, refRssi) by RANSAC against ground truth on all-but-one capture, then score the
    /// held-out capture with that calibration vs the as-captured baseline. Honest generalization —
    /// the scored capture never contributed to its own calibration.
    /// </summary>
    public static int CalVal(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var files = new List<string>();
        double ransacDb = 6.0, step = 1.0, stale = 30.0;
        string locator = "neldermead";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ransac": ransacDb = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--step": step = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--stale": stale = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--locator": locator = args[++i]; break;
                default: if (File.Exists(args[i])) files.Add(args[i]); break;
            }
        }
        if (files.Count < 2) { stderr.WriteLine("calval needs >=2 capture files (leave-one-out)."); return 2; }

        var caps = files.Select(f => (name: Path.GetFileName(f), cap: LoadCapture(f)))
                        .Where(x => x.cap?.Nodes?.Length > 0 && x.cap.Messages?.Length > 0 && x.cap.Positions?.Length > 0)
                        .Select(x => (x.name, cap: x.cap!)).ToList();
        if (caps.Count < 2) { stderr.WriteLine("Fewer than 2 valid captures (need nodes, messages, and ground-truth positions)."); return 2; }

        stdout.WriteLine($"Leave-one-capture-out calibration (RANSAC {ransacDb}dB, locator {locator})");
        stdout.WriteLine($"{"held-out",-44} {"fixes",6} {"baseline",9} {"fitted",9} {"delta",8}");
        stdout.WriteLine(new string('-', 80));

        foreach (var (name, held) in caps)
        {
            var trainers = caps.Where(c => !ReferenceEquals(c.cap, held)).Select(c => c.cap).ToList();
            var calib = FitCalibration(trainers, ransacDb);

            var engine = new ReplayEngine(held, step, stale);
            var baseErr = engine.Score(locator, m => m.Distance);
            var calErr = engine.Score(locator,
                m => calib.TryGetValue(m.Node, out var c) ? RecomputeAbs(m, c.absorption, c.refRssi) : m.Distance);

            if (baseErr.Count == 0) { stdout.WriteLine($"{name,-44} {0,6}  (no fixes)"); continue; }
            double b = Median(baseErr), f = Median(calErr);
            stdout.WriteLine($"{name,-44} {baseErr.Count,6} {b,9:0.000} {f,9:0.000} {f - b,8:+0.000;-0.000}");
        }

        // Show the calibration fitted from ALL captures (what you'd ship), with inlier counts.
        stdout.WriteLine();
        stdout.WriteLine("Per-node calibration fitted from ALL captures:");
        stdout.WriteLine($"{"node",-22} {"pts",5} {"inliers",8} {"absorption",11} {"refRssi",9}");
        stdout.WriteLine(new string('-', 60));
        var all = FitCalibration(caps.Select(c => c.cap).ToList(), ransacDb);
        foreach (var (node, c) in all.OrderBy(kv => kv.Key))
            stdout.WriteLine($"{node,-22} {c.total,5} {c.inliers,8} {c.absorption,11:0.00} {c.refRssi,9:0.0}");

        return 0;
    }

    private static Dictionary<string, (double absorption, double refRssi, int inliers, int total)> FitCalibration(
        List<Capture> caps, double ransacDb)
    {
        // Pool (log10(trueDist), rssi) per node across all training captures.
        var pts = new Dictionary<string, List<(double x, double y)>>();
        foreach (var cap in caps)
        {
            var positions = cap.Positions!.OrderBy(p => p.T).ToList();
            var meta = cap.Nodes!.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var m in cap.Messages!)
            {
                if (!meta.TryGetValue(m.Node, out var nd)) continue;
                var truth = TruthAt(positions, m.T);
                double d = Math.Sqrt(Math.Pow(nd.Point[0] - truth.X, 2) + Math.Pow(nd.Point[1] - truth.Y, 2) + Math.Pow(nd.Point[2] - truth.Z, 2));
                if (d < 0.3) continue;
                if (!pts.TryGetValue(m.Node, out var list)) pts[m.Node] = list = new List<(double, double)>();
                list.Add((Math.Log10(d), m.Rssi));
            }
        }

        var result = new Dictionary<string, (double, double, int, int)>();
        foreach (var (node, ps) in pts)
        {
            var fit = RansacLine(ps, ransacDb);
            if (fit == null) continue;
            var (a, slope, inliers) = fit.Value;
            double absorption = -slope / 10.0;            // rssi = refRssi - 10*absorption*log10(d)
            if (absorption < 1.5 || absorption > 6.0) continue; // nonphysical fit -> skip (fall back to as-captured)
            result[node] = (absorption, a, inliers, ps.Count);
        }
        return result;
    }

    // RANSAC line fit y = a + b*x. Returns (a, b, inlierCount) or null if no usable model.
    private static (double a, double b, int inliers)? RansacLine(List<(double x, double y)> pts, double thr)
    {
        int n = pts.Count;
        if (n < 4 || pts.Select(p => Math.Round(p.x, 3)).Distinct().Count() < 2) return null;

        int bestCount = -1; double bestA = 0, bestB = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = pts[j].x - pts[i].x;
                if (Math.Abs(dx) < 1e-6) continue;
                double b = (pts[j].y - pts[i].y) / dx;
                double a = pts[i].y - b * pts[i].x;
                int count = pts.Count(p => Math.Abs(p.y - (a + b * p.x)) <= thr);
                if (count > bestCount) { bestCount = count; bestA = a; bestB = b; }
            }
        if (bestCount < 4) return null;

        // Least-squares refit on the consensus inliers.
        var inl = pts.Where(p => Math.Abs(p.y - (bestA + bestB * p.x)) <= thr).ToList();
        double mx = inl.Average(p => p.x), my = inl.Average(p => p.y);
        double sxx = inl.Sum(p => (p.x - mx) * (p.x - mx)), sxy = inl.Sum(p => (p.x - mx) * (p.y - my));
        if (Math.Abs(sxx) < 1e-9) return (bestA, bestB, inl.Count);
        double bb = sxy / sxx, aa = my - bb * mx;
        return (aa, bb, inl.Count);
    }

    private static Pos TruthAt(List<Pos> sorted, DateTime t)
    {
        Pos current = sorted[0];
        foreach (var p in sorted) { if (p.T <= t) current = p; else break; }
        return current;
    }

    private static Capture? LoadCapture(string file)
        => JsonSerializer.Deserialize<Capture>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    private static double Percentile(List<double> xs, double p)
    {
        var s = xs.OrderBy(x => x).ToList();
        if (s.Count == 1) return s[0];
        double rank = p / 100.0 * (s.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return lo == hi ? s[lo] : s[lo] + (rank - lo) * (s[hi] - s[lo]);
    }

    // ---- capture DTOs (match the lowercase record-parameter names in DeviceCaptureService) ----
    private sealed class Capture
    {
        public string? DeviceId { get; set; }
        public Node[]? Nodes { get; set; }
        public Pos[]? Positions { get; set; }
        public Msg[]? Messages { get; set; }
    }

    public sealed class Node
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public double[] Point { get; set; } = new double[3];
    }

    public sealed class Pos
    {
        public DateTime T { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class Msg
    {
        public DateTime T { get; set; }
        public string Node { get; set; } = "";
        public double Distance { get; set; }
        public double Rssi { get; set; }
        public double RefRssi { get; set; }
    }

    /// <summary>
    /// Builds the in-memory Floor/State/Nodes once, then re-solves per time-step fix with
    /// freshly recomputed distances. Mirrors how the live locator consumes last-known-per-node.
    /// </summary>
    private sealed class ReplayEngine
    {
        private readonly Capture _cap;
        private readonly double _step;
        private readonly double _stale;
        private readonly Config _config;
        private readonly ConfigFloor _configFloor;
        private readonly Dictionary<string, Node> _nodeMeta;
        private readonly Dictionary<string, List<Msg>> _byNode;
        private readonly List<Pos> _positions;
        private readonly DateTime _start;
        private readonly DateTime _end;

        public ReplayEngine(Capture cap, double step, double stale)
        {
            _cap = cap;
            _step = step;
            _stale = stale;
            _nodeMeta = cap.Nodes!.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            _positions = cap.Positions!.OrderBy(p => p.T).ToList();
            _byNode = cap.Messages!.GroupBy(m => m.Node, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.T).ToList(), StringComparer.OrdinalIgnoreCase);
            _start = cap.Messages!.Min(m => m.T);
            _end = cap.Messages!.Max(m => m.T);

            // Floor bounds enclosing all node + truth points, with a margin.
            const double margin = 2.0;
            var xs = cap.Nodes!.Select(n => n.Point[0]).Concat(_positions.Select(p => p.X)).ToList();
            var ys = cap.Nodes!.Select(n => n.Point[1]).Concat(_positions.Select(p => p.Y)).ToList();
            var zs = cap.Nodes!.Select(n => n.Point[2]).Concat(_positions.Select(p => p.Z)).ToList();
            _configFloor = new ConfigFloor
            {
                Name = "replay",
                Bounds = new[]
                {
                    new[] { xs.Min() - margin, ys.Min() - margin, zs.Min() - margin },
                    new[] { xs.Max() + margin, ys.Max() + margin, zs.Max() + margin }
                }
            };
            _config = new Config { Floors = new[] { _configFloor } };
        }

        // The production locators mutate the State/Node graph they're handed (e.g. node RxNodes,
        // device scenarios), so a shared world leaks between runs. Build a fresh, isolated world
        // per fix instead.
        private (State state, Floor floor) BuildWorld()
        {
            var floor = new Floor();
            floor.Update(_config, _configFloor);
            var state = new State(new MockConfigLoader(_config), new MockNodeTelemetryStore());
            foreach (var n in _cap.Nodes!)
            {
                var node = new Node_(n.Id);
                var configNode = new ConfigNode { Name = n.Id, Id = n.Id, Point = new[] { n.Point[0], n.Point[1], n.Point[2] } };
                node.Update(_config, configNode, new[] { floor });
                state.Nodes[n.Id] = node;
            }
            return (state, floor);
        }

        // Alias so the file can use the production Node type without colliding with the DTO Node.
        private sealed class Node_ : ESPresense.Models.Node
        {
            public Node_(string id) : base(id, NodeSourceType.Config) { }
        }

        public List<double> Score(string locatorName, Func<Msg, double> distFn, double? ransacThreshold = null)
        {
            var errors = new List<double>();
            for (var t = _start; t <= _end; t = t.AddSeconds(_step))
            {
                var fixDistances = new Dictionary<string, double>();
                foreach (var (nodeId, msgs) in _byNode)
                {
                    var latest = LatestAtOrBefore(msgs, t);
                    if (latest == null) continue;
                    if ((t - latest.T).TotalSeconds > _stale) continue;
                    fixDistances[nodeId] = distFn(latest);
                }
                if (fixDistances.Count < 3) continue;

                var est = ransacThreshold is double thr
                    ? LocateRansac(locatorName, fixDistances, thr)
                    : Locate(locatorName, fixDistances);
                if (est == null) continue;

                var truth = TruthAt(t);
                double dx = est.Value.X - truth.X, dy = est.Value.Y - truth.Y;
                errors.Add(Math.Sqrt(dx * dx + dy * dy));
            }
            return errors;
        }

        public IEnumerable<BiasRow> PerNodeBias()
        {
            var rows = new List<BiasRow>();
            foreach (var (nodeId, msgs) in _byNode)
            {
                if (!_nodeMeta.TryGetValue(nodeId, out var meta)) continue;
                var biases = new List<double>();
                var impliedAbs = new List<double>();
                foreach (var m in msgs)
                {
                    var truth = TruthAt(m.T);
                    double trueDist = Math.Sqrt(
                        Math.Pow(meta.Point[0] - truth.X, 2) +
                        Math.Pow(meta.Point[1] - truth.Y, 2) +
                        Math.Pow(meta.Point[2] - truth.Z, 2));
                    if (trueDist < 0.1) continue;
                    biases.Add(m.Distance - trueDist);
                    // absorption that would have made measured distance equal the true distance
                    double log = Math.Log10(trueDist);
                    if (Math.Abs(log) > 1e-3) impliedAbs.Add((m.RefRssi - m.Rssi) / (10.0 * log));
                }
                if (biases.Count == 0) continue;
                rows.Add(new BiasRow(nodeId, biases.Count, biases.Average(),
                    biases.Average(Math.Abs), impliedAbs.Count > 0 ? impliedAbs.Average() : double.NaN));
            }
            return rows.OrderByDescending(r => Math.Abs(r.MeanBias));
        }

        public readonly record struct BiasRow(string Node, int Count, double MeanBias, double MeanAbs, double ImpliedAbsorption);

        private Point3D? Locate(string locatorName, Dictionary<string, double> distances)
        {
            // Fresh, isolated world + device per fix: we want the locator's raw single-shot output,
            // with no carried-over location and no cross-run state leakage.
            var (state, floor) = BuildWorld();
            var device = new Device("replay-device", "replay", TimeSpan.FromSeconds(30));
            var now = DateTime.UtcNow;
            foreach (var (nodeId, dist) in distances)
            {
                if (!state.Nodes.TryGetValue(nodeId, out var node)) continue;
                device.Nodes[nodeId] = new DeviceToNode(device, node) { Distance = Math.Max(0.1, dist), LastHit = now };
            }

            ILocate locator = locatorName.Trim().ToLowerInvariant() switch
            {
                "neldermead" or "nm" => new NelderMeadMultilateralizer(device, floor, state),
                "gaussnewton" or "gn" => new GaussNewtonMultilateralizer(device, floor, state),
                "bfgs" => new BfgsMultilateralizer(device, floor, state),
                "mle" => new MLEMultilateralizer(device, floor, state),
                _ => new GaussNewtonMultilateralizer(device, floor, state)
            };

            var scenario = new Scenario(_config, locator, "replay") { Confidence = 0 };
            return locator.Locate(scenario) ? scenario.Location : (Point3D?)null;
        }

        /// <summary>
        /// Consensus-based outlier rejection: fit on each minimal 3-node subset, count how many
        /// of ALL nodes agree with that fit (|predicted - measured| &lt;= threshold), keep the
        /// largest consensus set, then refit on those inliers. Drops persistent NLOS outliers
        /// (e.g. a node reading several metres long through a wall) that least-squares would
        /// otherwise smear across the whole fix.
        /// </summary>
        private Point3D? LocateRansac(string locatorName, Dictionary<string, double> distances, double threshold)
        {
            if (distances.Count <= 4) return Locate(locatorName, distances); // too few to spare any

            var ids = distances.Keys.ToList();
            HashSet<string>? bestInliers = null;

            foreach (var sample in Combinations(ids, 3))
            {
                var subset = sample.ToDictionary(id => id, id => distances[id]);
                var est = Locate(locatorName, subset);
                if (est == null) continue;

                var inliers = new HashSet<string>();
                foreach (var id in ids)
                {
                    if (!_nodeMeta.TryGetValue(id, out var meta)) continue;
                    double predicted = Math.Sqrt(
                        Math.Pow(meta.Point[0] - est.Value.X, 2) +
                        Math.Pow(meta.Point[1] - est.Value.Y, 2) +
                        Math.Pow(meta.Point[2] - est.Value.Z, 2));
                    if (Math.Abs(predicted - distances[id]) <= threshold) inliers.Add(id);
                }
                if (bestInliers == null || inliers.Count > bestInliers.Count) bestInliers = inliers;
            }

            // Refit on the consensus set (fall back to all nodes if consensus is too small).
            var keep = bestInliers != null && bestInliers.Count >= 3
                ? distances.Where(kv => bestInliers.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
                : distances;
            return Locate(locatorName, keep);
        }

        private static IEnumerable<List<string>> Combinations(List<string> items, int k)
        {
            var idx = Enumerable.Range(0, k).ToArray();
            int n = items.Count;
            if (k > n) yield break;
            while (true)
            {
                yield return idx.Select(i => items[i]).ToList();
                int p = k - 1;
                while (p >= 0 && idx[p] == n - k + p) p--;
                if (p < 0) yield break;
                idx[p]++;
                for (int j = p + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
            }
        }

        private Pos TruthAt(DateTime t)
        {
            Pos current = _positions[0];
            foreach (var p in _positions)
            {
                if (p.T <= t) current = p;
                else break;
            }
            return current;
        }

        private static Msg? LatestAtOrBefore(List<Msg> sorted, DateTime t)
        {
            Msg? result = null;
            foreach (var m in sorted)
            {
                if (m.T <= t) result = m;
                else break;
            }
            return result;
        }
        // (Msg is a reference type; the nullable annotation above is a reference-nullable.)
    }
}
