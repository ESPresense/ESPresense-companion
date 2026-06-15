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

        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--locator": locator = args[++i]; break;
                case "--absorption": absorptions.Add(double.Parse(args[++i], CultureInfo.InvariantCulture)); break;
                case "--ref-delta": refDelta = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--step": step = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--stale": stale = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--calibration": calibrationFile = args[++i]; break;
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

        stdout.WriteLine($"{"locator",-12} {"calibration",-20} {"fixes",6} {"median",8} {"p95",8} {"mean",8}");
        stdout.WriteLine(new string('-', 66));
        foreach (var loc in locators)
        {
            foreach (var (label, distFn) in calibrations)
            {
                var errors = engine.Score(loc, distFn);
                if (errors.Count == 0)
                {
                    stdout.WriteLine($"{loc,-12} {label,-20} {0,6}   (no locatable fixes)");
                    continue;
                }
                stdout.WriteLine($"{loc,-12} {label,-20} {errors.Count,6} {Median(errors),8:0.000} {Percentile(errors, 95),8:0.000} {errors.Average(),8:0.000}");
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

        public List<double> Score(string locatorName, Func<Msg, double> distFn)
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

                var est = Locate(locatorName, fixDistances);
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
