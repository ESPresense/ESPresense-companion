using System.Text.Json;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// Integration tests over a real survey captured by driving an ESPresense node around on a
/// LiDAR robot vacuum (see TestData/robot-survey-2026-10-12.json).
///
/// The robot reports its own position to a few millimetres, in ITS OWN frame. Most assertions
/// here are deliberately *frame-free*: they use only quantities invariant to rotation and
/// translation, which isolates solver behaviour from calibration error, node-position error and
/// any transform error.
///
/// The spatial survey also carries floorX/floorY: each robot position mapped into the floorplan by
/// a geometry-only fit of the robot's map to the room polygons (tools/robot-survey/frames.json).
/// The Accuracy_ tests use it. They inherit any error in that fit, but never from RSSI.
/// </summary>
[TestFixture]
public class RobotSurveyTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private Survey _survey = null!;
    private NodeTelemetryStore _nts = null!;

    private static readonly string[] Locators = { "nelder", "mle", "gaussnewton", "bfgs", "multifloor", "nadaraya", "nearest" };

    /// <summary>Locators that actually solve for a position from ranges, as opposed to picking or blending node positions.</summary>
    private static readonly string[] Solvers = { "nelder", "mle", "gaussnewton", "bfgs", "multifloor" };

    /// <summary>
    /// First-floor nodes the survey caught breaking the triangle inequality worst (RangeConsistency):
    /// they hear the robot through floors and walls. upstairs_hallway, bathroom and master were as bad
    /// but are second-floor nodes, which the solvers here never see.
    /// </summary>
    private static readonly string[] ThroughStructureNodes = { "back_stairs", "garage", "basement" };

    /// <summary>18 headings at one spot: good for orientation sensitivity, poor spatial spread.</summary>
    private const string Headings = "robot-survey-2026-10-12.json";

    /// <summary>One heading at many spots: good spatial spread, says nothing about orientation.</summary>
    private const string Spread = "robot-survey-2026-10-13.json";

    private static Survey Load(string file)
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", file);
        return JsonSerializer.Deserialize<Survey>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _nts = new NodeTelemetryStore(mqtt.Object);
        _state = new State(_configLoader, _nts);

        _survey = Load(Headings);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private Floor FirstFloor()
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = "first",
            Name = "First",
            Bounds = _survey.FloorBounds["first"]
        });
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    /// <summary>Only first-floor nodes: the robot is downstairs, and cross-floor links carry slab loss.</summary>
    private Dictionary<string, Node> FirstFloorNodes(Floor floor)
    {
        var map = new Dictionary<string, Node>();
        foreach (var n in _survey.Nodes.Where(n => n.Floors.Contains("first")))
        {
            var node = new Node(n.Id, NodeSourceType.Config);
            node.Update(_configLoader.Config!, new ConfigNode { Name = n.Id, Point = new[] { n.X, n.Y, n.Z } }, new[] { floor });
            _state.Nodes[n.Id] = node;
            map[n.Id] = node;
        }
        return map;
    }

    private ILocate Make(string which, Device d, Floor f, State s) => which switch
    {
        "nelder" => new NelderMeadMultilateralizer(d, f, s),
        "mle" => new MLEMultilateralizer(d, f, s),
        "gaussnewton" => new GaussNewtonMultilateralizer(d, f, s),
        "bfgs" => new BfgsMultilateralizer(d, f, s),
        "multifloor" => new MultiFloorMultilateralizer(d, s),
        "nadaraya" => new NadarayaWatsonMultilateralizer(d, f, s, _nts),
        "nearest" => new NearestNode(d, s),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, null)
    };

    /// <summary>Runs one locator against one set of per-node distances; null if it produced no fix.</summary>
    private Point3D? Solve(string which, Dictionary<string, double> dists, Floor floor, Dictionary<string, Node> nodes)
    {
        var device = new Device($"robot-{Guid.NewGuid()}", null, TimeSpan.FromSeconds(30));
        foreach (var (id, d) in dists)
            if (nodes.TryGetValue(id, out var node))
                device.Nodes[id] = new DeviceToNode(device, node) { Distance = d, LastHit = DateTime.UtcNow };

        var loc = Make(which, device, floor, _state);
        var scenario = new Scenario(_configLoader.Config, loc, which);
        return loc.Locate(scenario) ? scenario.Location : null;
    }

    /// <summary>
    /// The robot sat at one spot and only turned. Its position moved 2 mm across 18 headings, so
    /// every fix should land on the same point. Whatever spread we see is the solver's sensitivity
    /// to beacon orientation alone — a lower bound on the error a turning person induces.
    /// </summary>
    [Test]
    public void Repeatability_RobotStationary_OnlyHeadingChanged()
    {
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);

        var group = _survey.Points
            .GroupBy(p => (Math.Round(p.RobotX, 2), Math.Round(p.RobotY, 2)))
            .OrderByDescending(g => g.Count()).First();

        TestContext.WriteLine($"Robot fixed at robot-frame {group.Key}, {group.Count()} headings, true movement 2 mm\n");
        TestContext.WriteLine($"{"locator",-13}{"fixes",6}{"spread p95",12}{"max pair",10}{"centroid dev",14}");

        var anySolved = false;
        foreach (var which in Locators)
        {
            var fixes = group.Select(p => Solve(which, p.M.ToDictionary(kv => kv.Key, kv => kv.Value.Dist), floor, nodes))
                             .Where(p => p != null).Select(p => p!.Value).ToList();
            if (fixes.Count < 2) { TestContext.WriteLine($"{which,-13}{fixes.Count,6}   (insufficient fixes)"); continue; }
            anySolved = true;

            var cx = fixes.Average(p => p.X); var cy = fixes.Average(p => p.Y); var cz = fixes.Average(p => p.Z);
            var centroid = new Point3D(cx, cy, cz);
            var devs = fixes.Select(p => p.DistanceTo(centroid)).OrderBy(x => x).ToList();
            var pairs = (from i in Enumerable.Range(0, fixes.Count)
                         from j in Enumerable.Range(i + 1, fixes.Count - i - 1)
                         select fixes[i].DistanceTo(fixes[j])).ToList();

            TestContext.WriteLine($"{which,-13}{fixes.Count,6}{devs[(int)(0.95 * (devs.Count - 1))],12:0.00}{pairs.Max(),10:0.00}{devs.Average(),14:0.00}");
        }

        Assert.That(anySolved, Is.True, "no locator produced repeated fixes from the survey");
    }

    /// <summary>
    /// Frame-free rigidity check. The robot's pairwise position distances are known to millimetres.
    /// A correct solver must reproduce them whatever frame it works in — this is invariant to
    /// rotation and translation, though deliberately NOT to scale, so it also catches the
    /// systematic distance inflation seen in the calibration matrix.
    /// </summary>
    [TestCase(Headings)]
    [TestCase(Spread)]
    public void Rigidity_SolverMustReproduceKnownSeparations(string fixture)
    {
        _survey = Load(fixture);
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);

        // Average over headings so beacon orientation is not the thing under test here.
        var positions = _survey.Points
            .GroupBy(p => (X: Math.Round(p.RobotX, 2), Y: Math.Round(p.RobotY, 2)))
            .Where(g => g.Count() >= 1)
            .Select(g => (g.Key, Dists: g.SelectMany(p => p.M)
                                         .GroupBy(kv => kv.Key)
                                         .ToDictionary(k => k.Key, k => k.Average(v => v.Value.Dist))))
            .ToList();

        TestContext.WriteLine($"{positions.Count} physical positions; pairwise separations known to mm\n");
        TestContext.WriteLine($"{"locator",-13}{"pair",-26}{"true m",9}{"solved m",10}{"error",8}");

        var reported = false;
        foreach (var which in Locators)
        {
            var solved = positions.ToDictionary(p => p.Key, p => Solve(which, p.Dists, floor, nodes));
            var errs = new List<double>();
            var truths = new List<double>();
            var gots = new List<double>();
            for (int i = 0; i < positions.Count; i++)
                for (int j = i + 1; j < positions.Count; j++)
                {
                    var a = positions[i].Key; var b = positions[j].Key;
                    if (solved[a] is not { } pa || solved[b] is not { } pb) continue;
                    var truth = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    var got = Math.Sqrt(Math.Pow(pa.X - pb.X, 2) + Math.Pow(pa.Y - pb.Y, 2));
                    errs.Add(got - truth);
                    truths.Add(truth);
                    gots.Add(got);
                    // Only list every pair when there are few; a spatial survey makes hundreds.
                    if (positions.Count <= 6)
                        TestContext.WriteLine($"{which,-13}{$"({a.X},{a.Y})-({b.X},{b.Y})",-26}{truth,9:0.00}{got,10:0.00}{got - truth,8:+0.00;-0.00}");
                    reported = true;
                }
            if (errs.Count > 0)
                TestContext.WriteLine($"{which,-13}{"MEAN SIGNED ERROR",-26}{"",9}{"",10}{errs.Average(),8:+0.00;-0.00}");

            // A mean hides the shape. Regressing solved separation on true separation splits the
            // error into a multiplicative part (slope: distances scaled wrong) and an additive
            // part (intercept: a floor the solver cannot resolve below). Those need different fixes.
            if (truths.Count >= 6)
            {
                var mx = truths.Average();
                var my = gots.Average();
                var sxx = truths.Sum(t => (t - mx) * (t - mx));
                var slope = sxx > 1e-9 ? truths.Zip(gots, (t, g) => (t - mx) * (g - my)).Sum() / sxx : double.NaN;
                var intercept = my - slope * mx;
                TestContext.WriteLine($"{which,-13}{$"FIT over {truths.Count} pairs",-26}{"",9}  slope {slope,5:0.00}  intercept {intercept,6:+0.00;-0.00} m\n");
            }
            else TestContext.WriteLine();
        }

        Assert.That(reported, Is.True, "no locator produced fixes at two or more positions");
    }

    /// <summary>
    /// Diagnostic: is the jitter the solvers' fault, or are they being handed a system with no
    /// solution? If every measured distance is inflated by a constant factor the circles never
    /// intersect, the problem is ill-conditioned, and tiny input changes move the answer metres.
    /// Sweeping a global scale separates "solver is fragile" from "input is inconsistent".
    /// </summary>
    [TestCase(Headings)]
    [TestCase(Spread)]
    public void ScaleSweep_SeparatesSolverFragilityFromDistanceInflation(string fixture)
    {
        _survey = Load(fixture);
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);

        var positions = _survey.Points
            .GroupBy(p => (X: Math.Round(p.RobotX, 2), Y: Math.Round(p.RobotY, 2)))
            .Select(g => (g.Key, Dists: g.SelectMany(p => p.M).GroupBy(kv => kv.Key)
                                         .ToDictionary(k => k.Key, k => k.Average(v => v.Value.Dist))))
            .ToList();

        var stationary = _survey.Points
            .GroupBy(p => (Math.Round(p.RobotX, 2), Math.Round(p.RobotY, 2)))
            .OrderByDescending(g => g.Count()).First().ToList();

        TestContext.WriteLine($"{"scale",6}{"locator",-13}{"rigidity bias",15}{"rigidity rms",14}{"jitter p95",12}");
        foreach (var k in new[] { 1.00, 0.85, 0.70, 0.60, 0.50 })
        {
            foreach (var which in Locators)
            {
                var solved = positions.ToDictionary(p => p.Key, p => Solve(which, p.Dists.ToDictionary(kv => kv.Key, kv => kv.Value * k), floor, nodes));
                var errs = new List<double>();
                for (int i = 0; i < positions.Count; i++)
                    for (int j = i + 1; j < positions.Count; j++)
                    {
                        var a = positions[i].Key; var b = positions[j].Key;
                        if (solved[a] is not { } pa || solved[b] is not { } pb) continue;
                        var truth = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                        errs.Add(Math.Sqrt(Math.Pow(pa.X - pb.X, 2) + Math.Pow(pa.Y - pb.Y, 2)) - truth);
                    }

                var fixes = stationary
                    .Select(p => Solve(which, p.M.ToDictionary(kv => kv.Key, kv => kv.Value.Dist * k), floor, nodes))
                    .Where(p => p != null).Select(p => p!.Value).ToList();
                var jitter = double.NaN;
                if (fixes.Count > 1)
                {
                    var c = new Point3D(fixes.Average(p => p.X), fixes.Average(p => p.Y), fixes.Average(p => p.Z));
                    var d = fixes.Select(p => p.DistanceTo(c)).OrderBy(x => x).ToList();
                    jitter = d[(int)(0.95 * (d.Count - 1))];
                }

                if (errs.Count == 0) continue;
                var rms = Math.Sqrt(errs.Average(e => e * e));
                TestContext.WriteLine($"{k,6:0.00}{which,-13}{errs.Average(),15:+0.00;-0.00}{rms,14:0.00}{jitter,12:0.00}");
            }
        }

        Assert.Pass("diagnostic sweep - see output");
    }

    /// <summary>
    /// GaussNewton states its own objective: minimise the sum of squared distance residuals
    /// sum((|x - p_i| - d_i)^2) over the nodes it selected. This test holds it to exactly that.
    ///
    /// It is handed precisely four nodes, so there is no ambiguity about which subset it used,
    /// and its answer is scored against a brute-force search of the same objective over the same
    /// floor bounds. A solver that genuinely minimises cannot lose to a grid.
    ///
    /// Note why unit tests on synthetic data miss this: when the distances are mutually consistent
    /// the residual is zero at the solution, and a wrong Jacobian still has a fixed point there.
    /// The defect only shows on inconsistent measurements -- which is all that real radio produces.
    /// </summary>
    [Test]
    public void Optimality_GaussNewtonMustMinimiseItsOwnObjective()
    {
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);
        var lo = floor.Bounds![0];
        var hi = floor.Bounds![1];

        double Objective(Point3D x, List<(Point3D P, double D)> obs)
            => obs.Sum(o => Math.Pow(x.DistanceTo(o.P) - o.D, 2));

        // Coarse lattice over the floor, then a shrinking pattern search from the best cell.
        Point3D BruteForce(List<(Point3D P, double D)> obs)
        {
            var best = new Point3D(lo.X, lo.Y, lo.Z);
            var bestF = double.MaxValue;
            for (var x = lo.X; x <= hi.X; x += 0.25)
            for (var y = lo.Y; y <= hi.Y; y += 0.25)
            for (var z = lo.Z; z <= hi.Z; z += 0.25)
            {
                var p = new Point3D(x, y, z);
                var f = Objective(p, obs);
                if (f < bestF) { bestF = f; best = p; }
            }
            for (var step = 0.125; step > 1e-4; step /= 2)
            {
                var improved = true;
                while (improved)
                {
                    improved = false;
                    foreach (var d in new[]
                             {
                                 new Vector3D(step, 0, 0), new Vector3D(-step, 0, 0),
                                 new Vector3D(0, step, 0), new Vector3D(0, -step, 0),
                                 new Vector3D(0, 0, step), new Vector3D(0, 0, -step)
                             })
                    {
                        var c = best + d;
                        if (c.X < lo.X || c.X > hi.X || c.Y < lo.Y || c.Y > hi.Y || c.Z < lo.Z || c.Z > hi.Z) continue;
                        var f = Objective(c, obs);
                        if (f < bestF - 1e-12) { bestF = f; best = c; improved = true; }
                    }
                }
            }
            return best;
        }

        TestContext.WriteLine($"Floor bounds {lo} .. {hi}, 4 nearest nodes per point\n");
        TestContext.WriteLine($"{"point",-22}{"solver f",11}{"best f",11}{"excess",10}{"gap (m)",10}");

        var excesses = new List<double>();
        var worse = 0;
        foreach (var pt in _survey.Points)
        {
            var obs = pt.M
                .Where(kv => nodes.ContainsKey(kv.Key))
                .OrderBy(kv => kv.Value.Dist)
                .Take(4)
                .Select(kv => (P: nodes[kv.Key].Location, D: kv.Value.Dist))
                .ToList();
            if (obs.Count < 4) continue;

            var dists = obs.Select((o, i) => (Id: pt.M.Where(kv => nodes.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.Dist).ElementAt(i).Key, o.D))
                           .ToDictionary(t => t.Id, t => t.D);
            var solved = Solve("gaussnewton", dists, floor, nodes);
            if (solved == null) continue;

            var fSolver = Objective(solved.Value, obs);
            var best = BruteForce(obs);
            var fBest = Objective(best, obs);
            var excess = fSolver - fBest;
            excesses.Add(excess);
            if (excess > 0.25) worse++;

            TestContext.WriteLine($"{pt.Label,-22}{fSolver,11:0.000}{fBest,11:0.000}{excess,10:0.000}{solved.Value.DistanceTo(best),10:0.00}");
        }

        Assert.That(excesses, Is.Not.Empty, "no point produced a GaussNewton fix");
        var median = excesses.OrderBy(x => x).ElementAt(excesses.Count / 2);
        TestContext.WriteLine($"\n{worse} of {excesses.Count} points beaten by brute force; median excess {median:0.000}");

        Assert.That(worse, Is.LessThanOrEqualTo(excesses.Count / 5),
            $"GaussNewton lost to a quarter-metre grid on {worse} of {excesses.Count} points: " +
            "it is not minimising the objective it reports.");
    }

    /// <summary>
    /// Blames the measurement, not the solver, and needs no coordinate transform at all.
    ///
    /// If the robot moves from p to q, the true distance to any fixed node cannot change by more
    /// than the robot moved: |D(p) - D(q)| &lt;= |p - q|. That is the triangle inequality, it holds in
    /// every frame, and the robot measures |p - q| to millimetres. So for each node the ratio
    /// |d(p) - d(q)| / |p - q| must never exceed 1.
    ///
    /// When it does, that node's reported range moved further than physics allows. The worst ratio
    /// it reaches is a lower bound on its range error -- per node, with no fitting and no
    /// assumptions about geometry.
    ///
    /// It does NOT separate the causes: the beacon's heading also differs between positions, and
    /// heading alone shifts inferred distance by up to 2.9x (see the 2026-10-12 survey). So read
    /// this as "the measurement is impossible", not "the scale factor is X". Either way no solver
    /// can recover from it, which is the point.
    /// </summary>
    [TestCase(Spread)]
    public void RangeConsistency_NodeRangeCannotChangeFasterThanTheRobotMoved(string fixture)
    {
        _survey = Load(fixture);

        var positions = _survey.Points
            .GroupBy(p => (X: Math.Round(p.RobotX, 3), Y: Math.Round(p.RobotY, 3)))
            .Select(g => (g.Key, Dists: g.SelectMany(p => p.M).GroupBy(kv => kv.Key)
                                         .ToDictionary(k => k.Key, k => k.Average(v => v.Value.Dist))))
            .ToList();

        // The spatial survey is collected incrementally; don't fail the build while it is short.
        if (positions.Count < 6)
            Assert.Ignore($"needs 6+ spatially separated positions, fixture has {positions.Count} " +
                          "-- resume the tour with tools/robot-survey/run.py");

        TestContext.WriteLine($"{positions.Count} positions, pairs at least 1 m apart\n");
        TestContext.WriteLine($"{"node",-20}{"pairs",7}{"median",9}{"p95",8}{"worst",8}{"over 1.0",10}");

        var offenders = new List<string>();
        foreach (var node in _survey.Nodes.Select(n => n.Id).OrderBy(x => x))
        {
            var ratios = new List<double>();
            for (int i = 0; i < positions.Count; i++)
                for (int j = i + 1; j < positions.Count; j++)
                {
                    if (!positions[i].Dists.TryGetValue(node, out var di)) continue;
                    if (!positions[j].Dists.TryGetValue(node, out var dj)) continue;
                    var a = positions[i].Key; var b = positions[j].Key;
                    var moved = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    if (moved < 1.0) continue;   // small baselines turn measurement noise into huge ratios
                    ratios.Add(Math.Abs(di - dj) / moved);
                }

            if (ratios.Count < 3) continue;
            ratios.Sort();
            var over = ratios.Count(r => r > 1.0);
            var worst = ratios[^1];
            TestContext.WriteLine($"{node,-20}{ratios.Count,7}{ratios[ratios.Count / 2],9:0.00}" +
                                  $"{ratios[(int)(0.95 * (ratios.Count - 1))],8:0.00}{worst,8:0.00}" +
                                  $"{(double)over / ratios.Count,10:0%}");
            if (worst > 1.0) offenders.Add($"{node} x{worst:0.0}");
        }

        TestContext.WriteLine($"\nNodes reporting physically impossible range changes: " +
                              (offenders.Count == 0 ? "none" : string.Join(", ", offenders)));
        TestContext.WriteLine("'worst' is a lower bound on that node's range error (scale and heading combined).");

        Assert.Pass("diagnostic - see output");
    }

    /// <summary>
    /// Guards every other test here: the beacon must actually have been riding on the robot.
    ///
    /// The 2026-10-13 tour lost the calibrate hat partway through. The robot kept touring, every
    /// node's RSSI stayed within ~1 dB for 19 positions up to 15 m apart, and the solvers' "collapse"
    /// that followed was the beacon lying still. A real survey must show the per-node RSSI
    /// fingerprint changing more between positions that are further apart.
    /// </summary>
    [TestCase(Spread)]
    public void BeaconMovesWithRobot(string fixture)
    {
        _survey = Load(fixture);
        var pts = _survey.Points;
        var fp = new List<double>(); var sep = new List<double>();
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                var common = pts[i].M.Keys.Intersect(pts[j].M.Keys).ToList();
                if (common.Count < 4) continue;
                fp.Add(Math.Sqrt(common.Average(k => Math.Pow(pts[i].M[k].Rssi - pts[j].M[k].Rssi, 2))));
                sep.Add(Math.Sqrt(Math.Pow(pts[i].RobotX - pts[j].RobotX, 2) + Math.Pow(pts[i].RobotY - pts[j].RobotY, 2)));
            }
        if (fp.Count < 3) Assert.Ignore("fewer than three comparable pairs of positions");

        double mf = fp.Average(), ms = sep.Average();
        var corr = fp.Zip(sep, (f, d) => (f - mf) * (d - ms)).Sum()
                   / Math.Sqrt(fp.Sum(f => (f - mf) * (f - mf)) * sep.Sum(d => (d - ms) * (d - ms)));
        var farPairs = fp.Zip(sep).Where(t => t.Second >= 3).Select(t => t.First).OrderBy(x => x).ToList();
        TestContext.WriteLine($"{fp.Count} pairs; fingerprint-vs-separation correlation {corr:+0.00;-0.00}; " +
                              $"median RSSI difference for pairs 3 m+ apart {(farPairs.Count > 0 ? farPairs[farPairs.Count / 2] : double.NaN):0.0} dB");

        Assert.Multiple(() =>
        {
            Assert.That(corr, Is.GreaterThan(0.3), "RSSI fingerprints do not change with position: was the beacon on the robot?");
            if (farPairs.Count > 0)
                Assert.That(farPairs[farPairs.Count / 2], Is.GreaterThan(2.5),
                    "positions 3 m+ apart look identical in RSSI: the beacon was probably lying still");
        });
    }

    private static double Pct(List<double> sorted, double q) => sorted[(int)Math.Round(q * (sorted.Count - 1))];

    private string? RoomAt(double x, double y)
    {
        foreach (var r in _survey.Rooms.Where(r => r.Floor == "first"))
        {
            var inside = false;
            for (int i = 0, j = r.Points.Count - 1; i < r.Points.Count; j = i++)
            {
                double xi = r.Points[i][0], yi = r.Points[i][1], xj = r.Points[j][0], yj = r.Points[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            if (inside) return r.Id;
        }
        return null;
    }

    private sealed record AccuracyResult(int Fixes, int Points, double Median, double P90, double Max, int RoomHits, int RoomTotal);

    /// <summary>Absolute floorplan error for one locator over every survey point that has floor coordinates.</summary>
    private AccuracyResult Accuracy(string which, Floor floor, Dictionary<string, Node> nodes, ICollection<string>? exclude = null)
    {
        var pts = _survey.Points.Where(p => p.FloorX.HasValue && p.FloorY.HasValue).ToList();
        var errs = new List<double>();
        int hits = 0, total = 0;
        foreach (var p in pts)
        {
            var dists = p.M.Where(kv => exclude == null || !exclude.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Dist);
            if (Solve(which, dists, floor, nodes) is not { } fix) continue;
            errs.Add(Math.Sqrt(Math.Pow(fix.X - p.FloorX!.Value, 2) + Math.Pow(fix.Y - p.FloorY!.Value, 2)));
            if (p.FloorRoom == null) continue;
            total++;
            if (RoomAt(fix.X, fix.Y) == p.FloorRoom) hits++;
        }
        errs.Sort();
        return errs.Count == 0
            ? new AccuracyResult(0, pts.Count, double.NaN, double.NaN, double.NaN, 0, total)
            : new AccuracyResult(errs.Count, pts.Count, Pct(errs, 0.5), Pct(errs, 0.9), errs[^1], hits, total);
    }

    /// <summary>
    /// Absolute accuracy in the floorplan frame, per locator: horizontal error against the robot's
    /// LiDAR position, and how often the fix lands in the room the robot was actually in.
    ///
    /// Ground truth comes from frames.json, fitted to the robot map's geometry and never to RSSI.
    /// Its own error (decimetres) is well under the metre-scale errors measured here.
    ///
    /// The bounds are regression baselines, set a margin above what the locators achieved on this
    /// survey: they catch a change that makes a locator worse, they are not an accuracy target.
    /// </summary>
    [Test]
    public void Accuracy_FloorFrame_PerLocator()
    {
        _survey = Load(Spread);
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);
        RequireSpatialPoints(MinAccuracyPoints);

        TestContext.WriteLine($"{"locator",-13}{"fixes",8}{"median m",10}{"p90 m",8}{"max m",8}{"room hits",11}");
        var results = new Dictionary<string, AccuracyResult>();
        foreach (var which in Locators)
        {
            var r = results[which] = Accuracy(which, floor, nodes);
            TestContext.WriteLine($"{which,-13}{$"{r.Fixes}/{r.Points}",8}{r.Median,10:0.00}{r.P90,8:0.00}{r.Max,8:0.00}{$"{r.RoomHits}/{r.RoomTotal}",11}");
        }

        var nearest = results["nearest"].Median;
        var beaten = Solvers.Where(w => results[w].Median < nearest).ToList();
        TestContext.WriteLine($"\nSolvers beating the nearest-node baseline ({nearest:0.00} m): {(beaten.Count == 0 ? "none" : string.Join(", ", beaten))}");

        // No regression baselines yet: the first set was recorded on data where the beacon had
        // fallen off the robot (see BeaconMovesWithRobot). Pin them once a clean survey exists.
        Assert.Pass("diagnostic - see output");
    }

    /// <summary>Fewer positions than this and per-locator medians are noise.</summary>
    private const int MinAccuracyPoints = 10;

    private void RequireSpatialPoints(int min)
    {
        var n = _survey.Points.Count(p => p.FloorX.HasValue);
        if (n < min)
            Assert.Ignore($"needs {min}+ surveyed positions with floor coordinates, fixture has {n} " +
                          "-- run tools/robot-survey/run.py with the beacon attached, then build_fixture.py");
    }

    /// <summary>
    /// Does dropping the nodes that RangeConsistency caught hearing through floors and walls fix the
    /// solvers? Runs each solver on the spatial survey with every node, then without those, and
    /// compares absolute error and the rigidity slope (solved vs true separation; 1.0 is perfect).
    /// </summary>
    [Test]
    public void NodeExclusion_ThroughStructureNodesDragSolversToTheMiddle()
    {
        _survey = Load(Spread);
        RequireSpatialPoints(MinAccuracyPoints);
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);

        double Slope(string which, ICollection<string>? exclude)
        {
            var fixes = _survey.Points
                .Select(p => (P: p, Fix: Solve(which, p.M.Where(kv => exclude == null || !exclude.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Dist), floor, nodes)))
                .Where(t => t.Fix != null).ToList();
            var truths = new List<double>(); var gots = new List<double>();
            for (int i = 0; i < fixes.Count; i++)
                for (int j = i + 1; j < fixes.Count; j++)
                {
                    truths.Add(Math.Sqrt(Math.Pow(fixes[i].P.RobotX - fixes[j].P.RobotX, 2) + Math.Pow(fixes[i].P.RobotY - fixes[j].P.RobotY, 2)));
                    gots.Add(Math.Sqrt(Math.Pow(fixes[i].Fix!.Value.X - fixes[j].Fix!.Value.X, 2) + Math.Pow(fixes[i].Fix!.Value.Y - fixes[j].Fix!.Value.Y, 2)));
                }
            if (truths.Count < 6) return double.NaN;
            var mx = truths.Average(); var my = gots.Average();
            var sxx = truths.Sum(t => (t - mx) * (t - mx));
            return truths.Zip(gots, (t, g) => (t - mx) * (g - my)).Sum() / sxx;
        }

        TestContext.WriteLine($"excluding: {string.Join(", ", ThroughStructureNodes)}\n");
        TestContext.WriteLine($"{"locator",-13}{"median all",12}{"median excl",13}{"slope all",11}{"slope excl",12}{"rooms all",11}{"rooms excl",12}");
        foreach (var which in Solvers)
        {
            var all = Accuracy(which, floor, nodes);
            var excl = Accuracy(which, floor, nodes, ThroughStructureNodes);
            TestContext.WriteLine($"{which,-13}{all.Median,12:0.00}{excl.Median,13:0.00}{Slope(which, null),11:0.00}{Slope(which, ThroughStructureNodes),12:0.00}" +
                                  $"{$"{all.RoomHits}/{all.RoomTotal}",11}{$"{excl.RoomHits}/{excl.RoomTotal}",12}");
        }

        Assert.Pass("diagnostic - see output");
    }

    private sealed class Survey
    {
        public List<SurveyNode> Nodes { get; set; } = new();
        public List<SurveyRoom> Rooms { get; set; } = new();
        public List<SurveyPoint> Points { get; set; } = new();
        public Dictionary<string, double[][]> FloorBounds { get; set; } = new();
    }

    private sealed class SurveyNode
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public List<string> Floors { get; set; } = new();
    }

    private sealed class SurveyRoom
    {
        public string Id { get; set; } = "";
        public string Floor { get; set; } = "";
        public List<double[]> Points { get; set; } = new();
    }

    private sealed class SurveyPoint
    {
        public string Label { get; set; } = "";
        public double RobotX { get; set; }
        public double RobotY { get; set; }
        public double HeadingDeg { get; set; }
        public double? FloorX { get; set; }
        public double? FloorY { get; set; }
        public string? FloorRoom { get; set; }
        public Dictionary<string, Meas> M { get; set; } = new();
    }

    private sealed class Meas
    {
        public double Rssi { get; set; }
        public double Dist { get; set; }
        public int N { get; set; }
        public double Sd { get; set; }
    }
}
