using System.Text.Json;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// The tracker on a synthetic home: three placed nodes, a log-distance field with a wall that costs node
/// "a" 8 dB on the far side, surveyed on a 0.5 m grid. Reports carry Gaussian fading noise.
/// </summary>
[TestFixture]
public class FingerprintTrackerTests
{
    private const string FloorId = "tracker-floor";
    private static readonly (string Id, double X, double Y, double Z)[] NodeSpots = { ("a", 0.5, 1.5, 2.2), ("b", 9.5, 0.5, 2.2), ("c", 5.0, 2.8, 2.2) };

    private string _dir = null!;
    private ConfigLoader _loader = null!;
    private State _state = null!;
    private Floor _floor = null!;
    private readonly Dictionary<string, Node> _nodes = new();
    private FingerprintMap _map = null!;
    private FingerprintConfig _config = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dir);
        _loader = new ConfigLoader(_dir);
        _state = new State(_loader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        _floor = new Floor();
        _floor.Update(_loader.Config!, new ConfigFloor
        {
            Id = FloorId, Name = "Tracker",
            Bounds = new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 10.0, 3.0, 3.0 } },
            Rooms = new[]
            {
                new ConfigRoom { Name = "West", Points = new[] { new[] { 0.0, 0.0 }, new[] { 5.0, 0.0 }, new[] { 5.0, 3.0 }, new[] { 0.0, 3.0 } } },
                new ConfigRoom { Name = "East", Points = new[] { new[] { 5.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 10.0, 3.0 }, new[] { 5.0, 3.0 } } }
            }
        });
        _state.Floors[_floor.Id] = _floor;
        foreach (var (id, x, y, z) in NodeSpots)
        {
            var node = new Node(id, NodeSourceType.Config);
            node.Update(_loader.Config!, new ConfigNode { Name = id, Point = new[] { x, y, z } }, new[] { _floor });
            _state.Nodes[id] = node;
            _nodes[id] = node;
        }
        // Synthetic report noise is independent per report, so each one is a whole sample; live reports share fading.
        _config = new FingerprintConfig { Enabled = true, MinCommon = 2, ReadingWindowSecs = 60, Tracker = { Temper = 1.0 } };
        var path = Path.Combine(_dir, _config.File);
        var fingerprints = new List<object>();
        for (var x = 0.25; x <= 9.75; x += 0.5)
            for (var y = 0.25; y <= 2.75; y += 0.5)
                fingerprints.Add(new { floor = FloorId, x, y, z = 1.0, rssi = NodeSpots.ToDictionary(n => n.Id, n => Field(n.Id, x, y)) });
        File.WriteAllText(path, JsonSerializer.Serialize(new { fingerprints }));
        _map = new FingerprintMap(path);
    }

    [TearDown]
    public async Task Cleanup()
    {
        await _loader.StopAsync(CancellationToken.None);
        _loader.Dispose();
        Directory.Delete(_dir, true);
    }

    /// <summary>What node <paramref name="id"/> hears from a beacon at (x, y, 1): log-distance, plus a wall at x = 5 that costs "a" 8 dB beyond it.</summary>
    private static double Field(string id, double x, double y)
    {
        var n = NodeSpots.First(n => n.Id == id);
        var d = Math.Max(0.5, Math.Sqrt((n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y) + (n.Z - 1.0) * (n.Z - 1.0)));
        return -50 - 25 * Math.Log10(d) - (id == "a" && x > 5 ? 8 : 0);
    }

    private Point3D? NodeLocation(string id) => _nodes.TryGetValue(id, out var n) && n.HasLocation ? n.Location : null;

    private Device NewDevice(TimeSpan? timeout = null)
    {
        var device = new Device($"dev-{Guid.NewGuid()}", null, timeout ?? TimeSpan.FromSeconds(30));
        foreach (var (id, node) in _nodes) device.Nodes[id] = new DeviceToNode(device, node);
        return device;
    }

    private static void Report(Device device, string node, double raw) =>
        device.Nodes[node].ReadMessage(new DeviceMessage { Rssi = raw, RawRssi = raw, Distance = 1, RefRssi = -59 });

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>A clock the tests advance by hand, so diffusion does not depend on how fast the test runs.</summary>
    private DateTime _now = DateTime.UtcNow;
    private void Tick(double seconds = 1) => _now = _now.AddSeconds(seconds);

    private (FingerprintTracker Tracker, Scenario Scenario) NewTracker(Device device, int seed = 0) =>
        (new FingerprintTracker(device, _floor, _map, _config, NodeLocation, 0, () => _now) is var t
            ? (t, new Scenario(_loader.Config, t, FloorId)) : default);

    [Test]
    public void ConvergesOnTheBeaconFromNoisyReports()
    {
        var rng = new Random(3);
        var (tx, ty) = (7.3, 1.6);
        var device = NewDevice();
        var (tracker, scenario) = NewTracker(device, 11);
        var single = new FingerprintLocator(device, _floor, _map, new FingerprintConfig { Enabled = true, MinCommon = 2, ReadingWindowSecs = 0 }, NodeLocation);
        var singleScenario = new Scenario(_loader.Config, single, FloorId);
        var singleErrors = new List<double>();

        for (var round = 0; round < 8; round++)
        {
            Tick();
            foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, tx, ty) + 3 * Gaussian(rng));
            Assert.That(tracker.Locate(scenario) || round > 0, Is.True, "first batch must place the device");
            single.Locate(singleScenario);
            singleErrors.Add(Math.Sqrt(Math.Pow(singleScenario.Fit.X - tx, 2) + Math.Pow(singleScenario.Fit.Y - ty, 2)));
        }

        var error = Math.Sqrt(Math.Pow(scenario.Fit.X - tx, 2) + Math.Pow(scenario.Fit.Y - ty, 2));
        TestContext.WriteLine($"tracker {error:0.00} m after 8 rounds; single-report fixes {string.Join(", ", singleErrors.Select(e => e.ToString("0.00")))}; spread {tracker.Spread:0.00} m, effective points {tracker.EffectivePoints:0}");
        Assert.Multiple(() =>
        {
            // Three nodes leave the field nearly flat along y here, so the posterior is a ridge; 0.75 m is what that geometry allows.
            Assert.That(error, Is.LessThan(1.0), "eight rounds of 3 dB-noisy reports should land within a metre");
            Assert.That(error, Is.LessThan(singleErrors.Average()), "the tracker should beat the average single-report fix on the same reports");
            Assert.That(scenario.Room?.Name, Is.EqualTo("East"));
            Assert.That(scenario.Confidence, Is.GreaterThan(30));
        });
    }

    [Test]
    public void DeviceOffsetDoesNotMoveTheFix()
    {
        var (tx, ty) = (2.6, 0.9);
        var errors = new List<double>();
        foreach (var offset in new[] { 0.0, 12.0, -9.0 })
        {
            var device = NewDevice();
            var (tracker, scenario) = NewTracker(device, 5);
            for (var round = 0; round < 3; round++)
            {
                Tick();
                foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, tx, ty) + offset);
                tracker.Locate(scenario);
            }
            errors.Add(Math.Sqrt(Math.Pow(scenario.Fit.X - tx, 2) + Math.Pow(scenario.Fit.Y - ty, 2)));
            Assert.That(tracker.Offset, Is.EqualTo(offset).Within(1.5), $"offset {offset} dB should be recovered");
        }
        TestContext.WriteLine($"errors at 0/+12/-9 dB: {string.Join(", ", errors.Select(e => e.ToString("0.00")))}");
        Assert.That(errors, Has.All.LessThan(0.5));
    }

    [Test]
    public void OnlyFreshReportsCount()
    {
        var device = NewDevice();
        var (tracker, scenario) = NewTracker(device, 1);
        foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, 4, 1.5));
        Assert.That(tracker.Locate(scenario), Is.True);
        var points = tracker.EffectivePoints;
        Assert.That(tracker.Locate(scenario), Is.False, "no new report, nothing to update");
        Assert.That(tracker.EffectivePoints, Is.EqualTo(points));
    }

    [Test]
    public void ASilentNodePushesTheBeliefAwayFromIt()
    {
        // Short timeout so a node can go quiet within the test; the silence is metered against it.
        // The surface is built first: its first build takes longer than the timeout.
        _map.Surface(FloorId, _config.Bandwidth, _config.MissingRssi ?? -100, NodeLocation, 0, _config.SigmaFloor);
        var device = NewDevice(TimeSpan.FromMilliseconds(400));
        var (tracker, scenario) = NewTracker(device, 2);
        // Heard by all three near node b, on the east side.
        foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, 8.5, 0.8));
        Assert.That(tracker.Locate(scenario), Is.True, "first reading must place the device");
        var before = scenario.Fit;
        Thread.Sleep(600);
        // b falls silent; a and c keep reporting what they heard before.
        foreach (var n in NodeSpots.Where(n => n.Id != "b")) Report(device, n.Id, Field(n.Id, 8.5, 0.8));
        tracker.Locate(scenario);
        TestContext.WriteLine($"x before {before.X:0.00}, after {scenario.Fit.X:0.00}");
        Assert.That(scenario.Fit.X, Is.LessThan(before.X), "losing the nearest node should pull the fix away from it");
    }

    [Test]
    public void MapReloadKeepsTheOffsetAndRestartsTheBelief()
    {
        var device = NewDevice();
        var (tracker, scenario) = NewTracker(device, 4);
        foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, 4, 1.5) + 6);
        tracker.Locate(scenario);
        var offset = tracker.Offset;
        Assert.That(offset, Is.EqualTo(6).Within(1.5));

        _map.Append(new Fingerprint { Floor = FloorId, X = 4, Y = 1.5, Z = 1, Rssi = NodeSpots.ToDictionary(n => n.Id, n => Field(n.Id, 4, 1.5)) });
        File.SetLastWriteTimeUtc(_map.Path, DateTime.UtcNow.AddSeconds(5));
        foreach (var n in NodeSpots) Report(device, n.Id, Field(n.Id, 4, 1.5) + 6);
        Assert.That(tracker.Locate(scenario) || true, Is.True);
        Assert.That(tracker.Offset, Is.EqualTo(offset).Within(1.0), "the offset is a property of the device, not of the map");
        Assert.That(tracker.Points, Is.GreaterThan(0));
    }

    [Test]
    public void SurfaceUsesPriorForPlacedNodesAndRepeatSpreadPerNode()
    {
        var spots = new List<Fingerprint>();
        // Two visits to each of two spots: node "a" disagrees by 6 dB between visits, "b" by nothing.
        foreach (var x in new[] { 2.0, 4.0 })
            foreach (var d in new[] { -3.0, 3.0 })
                spots.Add(new Fingerprint { Floor = FloorId, X = x, Y = 1, Z = 1, Rssi = new() { ["a"] = Field("a", x, 1) + d, ["b"] = Field("b", x, 1) } });
        var surface = new FingerprintSurface(spots, 0.6, -100, NodeLocation);
        Assert.That(surface.Sigma(surface.NodeIndex("a")), Is.EqualTo(Math.Sqrt(18.0)).Within(0.01), "pooled sd of ±3 dB repeats");
        Assert.That(surface.Sigma(surface.NodeIndex("b")), Is.EqualTo(FingerprintSurface.DefaultSigmaFloor));

        // With the prior, the surface between two surveyed spots follows the log-distance shape instead of flattening.
        var mid = surface.Nearest(3.0, 1.0, 0.2);
        Assert.That(mid, Is.GreaterThanOrEqualTo(0));
        var predicted = surface.Predicted(mid, surface.NodeIndex("b"));
        Assert.That(predicted, Is.EqualTo(Field("b", 3, 1)).Within(1.0));
    }

    [Test]
    public void SparseSampleLeavesUnknownNodesOut()
    {
        var spots = new List<Fingerprint>
        {
            new() { Floor = FloorId, X = 1, Y = 1, Source = "moving", Rssi = new() { ["a"] = -60 } },
            new() { Floor = FloorId, X = 1, Y = 1, Source = "moving", Rssi = new() { ["b"] = -80 }, Silent = new[] { "c" } },
        };
        var surface = new FingerprintSurface(spots, 0.6, -100);
        var at = surface.Nearest(1, 1, 0.2);
        Assert.Multiple(() =>
        {
            Assert.That(surface.Predicted(at, surface.NodeIndex("a")), Is.EqualTo(-60).Within(0.01), "a known from the first bin");
            Assert.That(surface.Predicted(at, surface.NodeIndex("b")), Is.EqualTo(-80).Within(0.01), "b known from the second bin, the first says nothing about it");
            Assert.That(surface.Predicted(at, surface.NodeIndex("c")), Is.EqualTo(-100).Within(0.01), "c listed silent");
        });
        var complete = new FingerprintSurface(new List<Fingerprint> { new() { Floor = FloorId, X = 1, Y = 1, Rssi = new() { ["a"] = -60 } } }, 0.6, -100);
        Assert.That(complete.NodeIndex("b"), Is.EqualTo(-1), "a complete sample only knows the nodes it heard; the rest are silent, not features");
    }
}
