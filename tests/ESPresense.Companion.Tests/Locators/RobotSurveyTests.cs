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
/// The robot reports its own position to a few millimetres, but in ITS OWN frame — we do not
/// have a trustworthy rigid transform into the floorplan frame. So every assertion here is
/// deliberately *frame-free*: it uses only quantities invariant to rotation and translation.
/// That is a feature, not a limitation — it isolates solver behaviour from calibration error,
/// from node-position error, and from the unknown transform.
/// </summary>
[TestFixture]
public class RobotSurveyTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private Survey _survey = null!;

    private static readonly string[] Locators = { "nelder", "mle", "gaussnewton" };

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));

        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "robot-survey-2026-10-12.json");
        _survey = JsonSerializer.Deserialize<Survey>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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

    private static ILocate Make(string which, Device d, Floor f, State s) => which switch
    {
        "nelder" => new NelderMeadMultilateralizer(d, f, s),
        "mle" => new MLEMultilateralizer(d, f, s),
        _ => new GaussNewtonMultilateralizer(d, f, s)
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
    [Test]
    public void Rigidity_SolverMustReproduceKnownSeparations()
    {
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
            for (int i = 0; i < positions.Count; i++)
                for (int j = i + 1; j < positions.Count; j++)
                {
                    var a = positions[i].Key; var b = positions[j].Key;
                    if (solved[a] is not { } pa || solved[b] is not { } pb) continue;
                    var truth = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    var got = Math.Sqrt(Math.Pow(pa.X - pb.X, 2) + Math.Pow(pa.Y - pb.Y, 2));
                    errs.Add(got - truth);
                    TestContext.WriteLine($"{which,-13}{$"({a.X},{a.Y})-({b.X},{b.Y})",-26}{truth,9:0.00}{got,10:0.00}{got - truth,8:+0.00;-0.00}");
                    reported = true;
                }
            if (errs.Count > 0)
                TestContext.WriteLine($"{which,-13}{"MEAN SIGNED ERROR",-26}{"",9}{"",10}{errs.Average(),8:+0.00;-0.00}\n");
        }

        Assert.That(reported, Is.True, "no locator produced fixes at two or more positions");
    }

    /// <summary>
    /// Diagnostic: is the jitter the solvers' fault, or are they being handed a system with no
    /// solution? If every measured distance is inflated by a constant factor the circles never
    /// intersect, the problem is ill-conditioned, and tiny input changes move the answer metres.
    /// Sweeping a global scale separates "solver is fragile" from "input is inconsistent".
    /// </summary>
    [Test]
    public void ScaleSweep_SeparatesSolverFragilityFromDistanceInflation()
    {
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

    private sealed class Survey
    {
        public List<SurveyNode> Nodes { get; set; } = new();
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

    private sealed class SurveyPoint
    {
        public string Label { get; set; } = "";
        public double RobotX { get; set; }
        public double RobotY { get; set; }
        public double HeadingDeg { get; set; }
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
