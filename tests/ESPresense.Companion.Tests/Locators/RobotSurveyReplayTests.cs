using System.Globalization;
using System.Text.Json;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// ESPA-479 replay harness: drives the REAL production pipeline (ScenarioFactory + ScenarioFusion,
/// the same entry points MultiScenarioLocator uses) over the committed-but-unmerged robot/LiDAR
/// "approach" survey fixture, one simulated device, one simulated tick per second, to compare
/// locating quality between whatever git ref this worktree is checked out to (main or the
/// fix/kalman-raw-fit-and-walking-speed PR branch).
///
/// This file is deliberately unmodified between the two refs it is copied into: it only calls
/// ScenarioFusion.Update and reads device.Location/.Room/.Floor, all of which exist with the same
/// shape on both. It does not re-implement locating, Kalman filtering or hold logic.
///
/// WHY THIS RUNS IN REAL WALL-CLOCK TIME (this is the harness's central methodological finding):
/// Device.UpdateLocation(Point3D) -> KalmanLocation.Update(newLocation) and
/// KalmanLocation.GetPrediction() both read DateTime.UtcNow INTERNALLY to compute the Kalman
/// filter's dt, with no overload that accepts an injected timestamp reachable from
/// ScenarioFusion.Update's public surface. (KalmanLocation *does* have a Update(location, now)
/// overload, but Device.UpdateLocation never calls it.) That means the device-level Kalman filter's
/// process-noise growth, velocity estimate and max-velocity clamp -- precisely what PR #1830 touches
/// ("keep up at walking pace") -- are paced by real elapsed wall-clock seconds between successive
/// ScenarioFusion.Update calls, not by whatever DateTime we pass as the `now` parameter. A detached/
/// accelerated simulated clock (now += 1s per tick, executed at full CPU speed) would make every
/// tick's dt collapse to a few milliseconds, effectively testing "what happens if 1200 ticks arrive
/// in under a second" rather than "40 dwells on a walking-speed survey" -- and it would ALSO break
/// Scenario.FitAt (new in this PR; stamped with real DateTime.UtcNow inside Scenario.UpdateLocation/
/// ResetLocation) against the `s.FitAt >= now` gate in ScenarioFusion.Update the moment the simulated
/// clock pulls more than an instant ahead of the real one -- which starves the device-level Kalman
/// filter on the PR branch specifically (device.UpdateLocation never runs), making the PR look frozen
/// for a harness reason that has nothing to do with the code under test.
///
/// So every tick here really does take ~1 wall-clock second (ESPA479_TICK_SECONDS, default 1.0):
/// `now = DateTime.UtcNow` is captured fresh immediately before each ScenarioFusion.Update call,
/// exactly as MultiScenarioLocator.ProcessDevice does in production, and DeviceToNode.LastHit is
/// also stamped with that same real `now` for nodes heard this tick. A 40-visit x 30-tick run takes
/// about 20 real minutes; set ESPA479_TICK_SECONDS=0 for a fast, temporally-invalid smoke test of
/// wiring/compilation only (its Kalman/hold numbers are not meaningful -- see the class comment
/// above for why). ESPA479_VISIT_OFFSET/ESPA479_MAX_VISITS select a [offset, offset+count) slice of
/// the survey and ESPA479_ONLY_SEED skips every other seed's TestCase immediately, so a single real-
/// time run can be chunked across several processes that each fit inside a bounded time budget --
/// chunk output file names carry the visit range so chunks never collide, and the chunks for one
/// build x seed are just concatenated back into one list before analysis.
///
/// Metrics are intentionally NOT computed in this file: each run dumps raw per-tick observations
/// (location, room, floor) plus the two enabled locators' raw per-tick Fit to a JSON file
/// (ESPA479_OUTPUT, one file per git-ref x seed), and a separate offline script merges the main-ref
/// and PR-ref runs to compute the final metrics and the per-visit table. That keeps this file to
/// "drive production code, record what came out" with no locator/Kalman/hold re-implementation.
/// </summary>
[TestFixture]
public class RobotSurveyReplayTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private Survey _survey = null!;
    private NodeTelemetryStore _nts = null!;

    private const string Approach = "robot-survey-approach.json";
    private const int TicksPerDwell = 30;

    private static double TickSeconds =>
        double.TryParse(Environment.GetEnvironmentVariable("ESPA479_TICK_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1.0;

    private static int MaxVisits =>
        int.TryParse(Environment.GetEnvironmentVariable("ESPA479_MAX_VISITS"), out var n) && n > 0 ? n : int.MaxValue;

    /// <summary>Visits to skip before starting, so a run can be chunked across several processes
    /// within a tool time budget while still only ever running at real wall-clock speed.</summary>
    private static int VisitOffset =>
        int.TryParse(Environment.GetEnvironmentVariable("ESPA479_VISIT_OFFSET"), out var n) && n > 0 ? n : 0;

    private static string OutputDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("ESPA479_OUTPUT_DIR");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "espa479-replay-out");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Which git ref this worktree is checked out to -- only used to name the output file.</summary>
    private static string BuildTag => Environment.GetEnvironmentVariable("ESPA479_BUILD_TAG") ?? "unknown";

    internal static string LoadFixtureText(string file)
    {
        var dir = Environment.GetEnvironmentVariable("ESPRESENSE_SURVEY_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "robot-survey");
        var path = Path.Combine(dir, file);
        if (!File.Exists(path))
            Assert.Ignore($"robot survey data not available: {path} not found");
        return File.ReadAllText(path);
    }

    private static Survey Load(string file) =>
        JsonSerializer.Deserialize<Survey>(LoadFixtureText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [SetUp]
    public async Task Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        // Load before State exists: a late load would merge the example config's rooms into our floor.
        await _configLoader.LoadAsync();
        _nts = new NodeTelemetryStore(mqtt.Object);
        _state = new State(_configLoader, _nts);

        // DT's soak config: multi_floor + nearest_node, each build's own shipped defaults otherwise.
        _configLoader.Config!.Locators.MultiFloor.Enabled = true;
        _configLoader.Config!.Locators.NearestNode.Enabled = true;
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
            Bounds = _survey.FloorBounds["first"],
            Rooms = _survey.Rooms.Where(r => r.Floor == "first").Select(r => new ConfigRoom
            {
                Id = r.Id, Name = r.Id, Points = r.Points.Select(q => q.ToArray()).ToArray()
            }).ToArray()
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

    /// <summary>A human teaching dwell: 90 s on the grid/top-up tours (labels "p"/"g"), 30 s elsewhere.
    /// Copied verbatim from the fixture branch's RobotSurveyTests.cs (not present on either main or
    /// the PR branch's current copy of that file) -- every label in this fixture is "v"/"w", so this
    /// always evaluates to 30 here; kept so SimulateDwellRssi matches its origin exactly.</summary>
    private static double HumanDwellSeconds(SurveyPoint p) => p.Label.StartsWith("p") || p.Label.StartsWith("g") ? 90 : 30;

    private static double GaussianNoise(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>
    /// Simulates a <paramref name="seconds"/>-long dwell at <paramref name="spot"/>: each node's raw rssi
    /// is the mean of however many reports arrive in that window -- Poisson at the node's rate over the
    /// survey's own (longer) dwell -- with sampling noise on that mean; a node that reports nothing in
    /// the window is missed entirely, the same way a real receiver would be. Copied from the fixture
    /// branch's RobotSurveyTests.cs (SimulateDwell), renamed to make clear it returns rssi, not distance.
    /// </summary>
    private static Dictionary<string, double> SimulateDwellRssi(SurveyPoint spot, double seconds, Random noise, ICollection<string> knownNodes)
    {
        var reading = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (node, m) in spot.M)
        {
            if (!knownNodes.Contains(node)) continue;
            var lambda = m.N / HumanDwellSeconds(spot) * seconds;
            var reports = 0;
            var l = Math.Exp(-lambda);
            var p = noise.NextDouble();
            while (p > l) { reports++; p *= noise.NextDouble(); }
            if (reports == 0) continue;
            reading[node] = m.Rssi + GaussianNoise(noise) * Math.Max(m.Sd, 1.0) / Math.Sqrt(reports);
        }
        return reading;
    }

    /// <summary>Firmware defaults (main/defaults.h): what a node reports before any calibration.</summary>
    private const double DefaultRefRssi = -59, DefaultAbsorption = 2.7;
    private static double RssiToDist(double rssi) => Math.Pow(10, (DefaultRefRssi - rssi) / (10 * DefaultAbsorption));

    private sealed record TickRecord(
        int Visit, string Label, string? FloorRoom, double FloorX, double FloorY, int Tick,
        double? X, double? Y, double? Z, string? Room, string? Floor,
        double? MultiFloorFitX, double? MultiFloorFitY, double? NearestFitX, double? NearestFitY);

    [Explicit("Long-running real-time replay harness (~20 min/seed); run manually with ESPA479_* env vars, see class doc comment")]
    [Category("LongRunning")]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Replay_FullPipeline_OverApproachSurvey(int seed)
    {
        // CLI test-case filtering on a parameterized display name hits vstest filter-syntax issues
        // with the parentheses; skip instantly instead so one process can still be pointed at a
        // single seed within a time-boxed chunk.
        var onlySeed = Environment.GetEnvironmentVariable("ESPA479_ONLY_SEED");
        if (!string.IsNullOrEmpty(onlySeed) && onlySeed != seed.ToString(CultureInfo.InvariantCulture))
            Assert.Ignore($"ESPA479_ONLY_SEED={onlySeed}, skipping seed {seed}");

        _survey = Load(Approach);
        var floor = FirstFloor();
        var nodes = FirstFloorNodes(floor);
        var knownNodeIds = nodes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var device = new Device($"replay-{Guid.NewGuid()}", null, TimeSpan.FromSeconds(30)) { Track = true };
        device.KalmanFilter.Settings = _state.KalmanSettings;
        // Built once, exactly like DeviceTracker does when a device first appears; never rebuilt.
        foreach (var scenario in _state.ScenarioFactory.Create(device)) device.Scenarios.Add(scenario);
        var multiFloorScenario = device.Scenarios.FirstOrDefault(s => s.Name == "MultiFloor");
        var nearestScenario = device.Scenarios.FirstOrDefault(s => s.Name == "NearestNode");

        var rng = new Random(seed);
        var filtering = new ConfigFiltering();
        var tickSeconds = TickSeconds;
        var offset = Math.Min(VisitOffset, _survey.Points.Count);
        var visitEnd = Math.Min(offset + MaxVisits, _survey.Points.Count);
        var records = new List<TickRecord>((visitEnd - offset) * TicksPerDwell);

        TestContext.WriteLine($"seed={seed} tickSeconds={tickSeconds} visits=[{offset},{visitEnd})/{_survey.Points.Count} buildTag={BuildTag}");

        for (var vi = offset; vi < visitEnd; vi++)
        {
            var point = _survey.Points[vi];
            for (var tick = 1; tick <= TicksPerDwell; tick++)
            {
                var tickStart = DateTime.UtcNow;

                // A node SimulateDwellRssi doesn't return for this tick is simply not heard this
                // second -- we do NOT force-update it, exactly as a real receiver would behave.
                var rssi = SimulateDwellRssi(point, 1.0, rng, knownNodeIds);
                var now = DateTime.UtcNow; // same instant used for every DeviceToNode touched this tick and for ScenarioFusion.Update, as production does in MultiScenarioLocator.ProcessDevice
                foreach (var (nodeId, r) in rssi)
                {
                    if (!nodes.TryGetValue(nodeId, out var node)) continue;
                    var dn = device.Nodes.GetOrAdd(nodeId, _ => new DeviceToNode(device, node));
                    dn.Rssi = r;
                    dn.Distance = RssiToDist(r);
                    dn.LastHit = now;
                    dn.Hits++;
                }

                ScenarioFusion.Update(device, filtering, now);

                records.Add(new TickRecord(
                    vi, point.Label, point.FloorRoom, point.FloorX ?? double.NaN, point.FloorY ?? double.NaN, tick,
                    device.Location?.X, device.Location?.Y, device.Location?.Z,
                    device.Room?.Id, device.Floor?.Id,
                    multiFloorScenario?.Fit.X, multiFloorScenario?.Fit.Y,
                    nearestScenario?.Fit.X, nearestScenario?.Fit.Y));

                if (tickSeconds > 0)
                {
                    var elapsed = DateTime.UtcNow - tickStart;
                    var remaining = TimeSpan.FromSeconds(tickSeconds) - elapsed;
                    if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
                }
            }
        }

        var chunkSuffix = offset > 0 || visitEnd < _survey.Points.Count ? $"-v{offset}_{visitEnd}" : "";
        var outPath = Path.Combine(OutputDir, $"{BuildTag}-seed{seed}{chunkSuffix}.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(records));
        TestContext.WriteLine($"wrote {records.Count} tick records to {outPath}");

        Assert.Pass($"recorded {records.Count} ticks to {outPath}");
    }

    private sealed class Survey
    {
        public List<SurveyNode> Nodes { get; set; } = new();
        public List<SurveyRoom> Rooms { get; set; } = new();
        public List<SurveyPoint> Points { get; set; } = new();
        public Dictionary<string, double[][]> FloorBounds { get; set; } = new();
        public SurveyExpect? Expect { get; set; }
    }

    private sealed class SurveyExpect
    {
        public List<string>? ThroughStructureNodes { get; set; }
        public Dictionary<string, double>? MedianErrorM { get; set; }
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

    /// <summary>One node's view of one spot: raw rssi, plus the sample count/spread SimulateDwellRssi needs.</summary>
    private sealed class Meas
    {
        public double Rssi { get; set; }
        public int N { get; set; }
        public double Sd { get; set; }
    }
}
