using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceResidualStoreTests
{
    private static readonly ConfigDeviceResidual Settings = new();
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] EightNodes = Enumerable.Range(1, 8).Select(i => $"n{i}").ToArray();

    /// <summary>One reading from each of <paramref name="nodes"/> at <paramref name="t"/>.</summary>
    private static void Heard(DeviceResidualStore store, string device, DateTime t, IEnumerable<string> nodes, double rssi = -70, double? rxAdj = 0, double refRssi = -59)
    {
        foreach (var n in nodes) store.Add(device, t, n, rssi, rxAdj, refRssi, Settings);
    }

    [Test]
    public void ReadingsInAWindow_PoolToTheMedianRawRssi_AtTheNewestRxAdj()
    {
        var store = new DeviceResidualStore();
        Heard(store, "phone", T0.AddSeconds(1), EightNodes.Skip(1));
        // n1 publishes -70, -72 (rxAdj 0) then -80 with rxAdj 5: raw -70, -72, -75, median raw -72.
        store.Add("phone", T0.AddSeconds(1), "n1", -70, 0, -59, Settings);
        store.Add("phone", T0.AddSeconds(4), "n1", -72, 0, -59, Settings);
        store.Add("phone", T0.AddSeconds(7), "n1", -80, 5, -59, Settings);

        var window = store.Windows(null, null, T0.AddSeconds(30)).Single();

        var n1 = window.Readings.Single(r => r.NodeId == "n1");
        Assert.That(n1.RxAdj, Is.EqualTo(5));
        Assert.That(n1.Rssi, Is.EqualTo(-77), "median raw -72, published with rxAdj 5");
        Assert.That(n1.AdjustedRssi(0), Is.EqualTo(-72), "re-based to rxAdj 0 it is the raw median");
        Assert.That(window.RefRssi, Is.EqualTo(-59));
        Assert.That(window.Start, Is.EqualTo(T0));
        Assert.That(window.End, Is.EqualTo(T0.AddSeconds(10)));
    }

    [Test]
    public void UnknownRxAdj_IsKeptAsPublished()
    {
        var store = new DeviceResidualStore();
        Heard(store, "phone", T0.AddSeconds(1), EightNodes, rssi: -70, rxAdj: null);

        var reading = store.Windows(null, null, T0.AddSeconds(30)).Single().Readings[0];

        Assert.That(reading.RxAdj, Is.Null);
        Assert.That(reading.AdjustedRssi(4), Is.EqualTo(-70), "an unknown rxAdj cannot be re-based, as Measure.GetAdjustedRssi");
    }

    [Test]
    public void WindowHeardByTooFewNodes_IsDropped()
    {
        var store = new DeviceResidualStore();
        Heard(store, "phone", T0.AddSeconds(1), EightNodes.Take(7));
        Heard(store, "phone", T0.AddSeconds(11), EightNodes);

        var windows = store.Windows(null, null, T0.AddSeconds(30));

        Assert.That(windows.Select(w => w.Start), Is.EqualTo(new[] { T0.AddSeconds(10) }));
    }

    [Test]
    public void Windows_AreReturnedOnceClosed_ByEndTime()
    {
        var store = new DeviceResidualStore();
        for (int i = 0; i < 5; i++) Heard(store, "phone", T0.AddSeconds(10 * i + 1), EightNodes);

        Assert.That(store.Windows(null, null, T0.AddSeconds(45)), Has.Count.EqualTo(4), "the window ending at 50 s is still open at 45 s");
        Assert.That(store.Windows(null, null, T0.AddSeconds(50)), Has.Count.EqualTo(5));
        Assert.That(store.Windows(T0.AddSeconds(20), T0.AddSeconds(40), T0.AddSeconds(60)).Select(w => w.End),
            Is.EqualTo(new[] { T0.AddSeconds(30), T0.AddSeconds(40) }), "after is exclusive, until inclusive");
    }

    [Test]
    public void OldWindows_ExpireAfterTheHistory()
    {
        var store = new DeviceResidualStore();
        var settings = new ConfigDeviceResidual { HistoryHours = 1 };
        foreach (var n in EightNodes) store.Add("phone", T0.AddSeconds(1), n, -70, 0, -59, settings);
        foreach (var n in EightNodes) store.Add("phone", T0.AddMinutes(50), n, -70, 0, -59, settings);

        Assert.That(store.Windows(null, null, T0.AddMinutes(59)), Has.Count.EqualTo(2));
        Assert.That(store.Windows(null, null, T0.AddMinutes(70)), Has.Count.EqualTo(1));
    }

    [Test]
    public void OverTheCap_TheBusiestDeviceIsThinnedEvenly_OthersKeepEverything()
    {
        var store = new DeviceResidualStore();
        var week = new ConfigDeviceResidual { HistoryHours = 24 * 7 }; // so nothing here expires by age
        var busy = DeviceResidualStore.MaxWindows + 10_000;
        for (int i = 0; i < busy; i++)
            foreach (var n in EightNodes) store.Add("tv", T0.AddSeconds(10 * i + 1), n, -70, 0, -59, week);
        for (int i = 0; i < 100; i++)
            foreach (var n in EightNodes) store.Add("phone", T0.AddSeconds(10 * i + 1), n, -70, 0, -59, week);

        var windows = store.Windows(null, null, T0.AddSeconds(10 * busy + 10));

        Assert.That(windows.Count, Is.LessThanOrEqualTo(DeviceResidualStore.MaxWindows));
        Assert.That(windows.Count(w => w.DeviceId == "phone"), Is.EqualTo(100));
        var tv = windows.Where(w => w.DeviceId == "tv").ToList();
        Assert.That(tv[^1].End, Is.EqualTo(T0.AddSeconds(10 * busy)), "the newest is kept");
        Assert.That(tv[0].End, Is.LessThan(T0.AddSeconds(10 * busy * 0.05)), "the history still reaches back to the start");
    }

    private static Node PlacedNode(string id, double x)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(new Config(), new ConfigNode { Name = id, Point = new[] { x, 0, 1 } }, Enumerable.Empty<Floor>());
        return node;
    }

    private static Device DeviceHeardBy(IEnumerable<Node> nodes, double rssi, DateTime at)
    {
        var device = new Device("phone", null, TimeSpan.FromSeconds(30));
        foreach (var node in nodes)
            device.Nodes[node.Id] = new DeviceToNode(device, node);
        Hear(device, rssi, at);
        return device;
    }

    /// <summary>A message from every node, stamped <paramref name="at"/> so the test does not depend on the clock.</summary>
    private static void Hear(Device device, double rssi, DateTime at)
    {
        foreach (var dn in device.Nodes.Values)
        {
            dn.ReadMessage(new DeviceMessage { Rssi = rssi, RssiRxAdj = 0, RefRssi = -59, Distance = 3 });
            dn.LastHit = at;
        }
    }

    [Test]
    public void Observe_TakesEachReadingOnce()
    {
        var store = new DeviceResidualStore();
        var device = DeviceHeardBy(EightNodes.Select((id, i) => PlacedNode(id, i)), -70, T0.AddSeconds(1));

        store.Observe(device, T0.AddSeconds(1), Settings);
        foreach (var dn in device.Nodes.Values) dn.Rssi = -90; // no new message: the same reading, re-located
        store.Observe(device, T0.AddSeconds(2), Settings);
        Hear(device, -80, T0.AddSeconds(3));
        store.Observe(device, T0.AddSeconds(3), Settings);

        var window = store.Windows(null, null, T0.AddMinutes(1)).Single();
        Assert.That(window.Readings.Select(r => r.Rssi), Is.All.EqualTo(-75), "median of -70 and -80; the re-located -90 was not a reading");
    }

    [Test]
    public void Observe_SkipsNodesWithoutAPosition()
    {
        var store = new DeviceResidualStore();
        var unplaced = new Node("loose", NodeSourceType.Discovered);
        var device = DeviceHeardBy(EightNodes.Take(7).Select((id, i) => PlacedNode(id, i)).Append(unplaced), -70, T0.AddSeconds(1));

        store.Observe(device, T0.AddSeconds(1), Settings);

        Assert.That(store.Windows(null, null, T0.AddMinutes(1)), Is.Empty, "seven positioned nodes are fewer than min_nodes");
    }

    [Test]
    public void Observe_AnchoredDevice_IsForgotten()
    {
        var store = new DeviceResidualStore();
        var device = DeviceHeardBy(EightNodes.Select((id, i) => PlacedNode(id, i)), -70, T0.AddSeconds(1));
        store.Observe(device, T0.AddSeconds(1), Settings);
        store.Flush(T0.AddSeconds(30));
        Assert.That(store.Count, Is.EqualTo(1));

        device.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), null, null));
        store.Observe(device, T0.AddSeconds(31), Settings);

        Assert.That(store.Count, Is.Zero, "an anchored device is ground truth for the node-to-node path, not a device residual");
    }
}

/// <summary>How device windows reach the optimizer's snapshots.</summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceWindowSnapshotTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        await _configLoader.LoadAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private void Collect(bool enabled, string optimizer)
    {
        _state.Config!.Optimization.Enabled = enabled;
        _state.Config!.Optimization.Optimizer = optimizer;
    }

    private void AddWindowEndingBefore(DateTime now)
    {
        var t = now.AddSeconds(-25);
        foreach (var n in Enumerable.Range(1, 8)) _state.DeviceResiduals.Add("phone", t, $"n{n}", -70, 0, -59, new ConfigDeviceResidual());
    }

    [TestCase(true, "device_residual", 1)]
    [TestCase(true, "per_node_absorption", 0)]
    [TestCase(false, "device_residual", 0)]
    public void ReadMeasures_CarriesDeviceWindows_OnlyForTheDeviceResidualOptimizer(bool enabled, string optimizer, int expected)
    {
        Collect(enabled, optimizer);
        AddWindowEndingBefore(DateTime.UtcNow);

        var os = _state.ReadMeasures(DateTime.UtcNow.AddMinutes(-5));

        Assert.That(os.DeviceWindows, Has.Count.EqualTo(expected));
        Assert.That(os.Geometry != null, Is.EqualTo(expected > 0));
    }

    [Test]
    public void ReadMeasures_WithoutSince_CarriesNoDeviceWindows()
    {
        Collect(true, "device_residual");
        AddWindowEndingBefore(DateTime.UtcNow);

        Assert.That(_state.ReadMeasures().DeviceWindows, Is.Empty);
    }

    [Test]
    public void EachSnapshot_CarriesTheWindowsThatClosedSinceThePreviousOne()
    {
        Collect(true, "device_residual");
        var snapshots = new OptimizationSnapshotStore();
        AddWindowEndingBefore(DateTime.UtcNow);

        var first = snapshots.Take(_state);
        var second = snapshots.Take(_state);

        Assert.That(first.DeviceWindows, Has.Count.EqualTo(1));
        Assert.That(second.DeviceWindows, Is.Empty, "a window belongs to one snapshot only");
    }

    [Test]
    public void TrainingSplit_PoolsTheTrainingSnapshotsDeviceWindows()
    {
        var geometry = new DeviceGeometry(new Dictionary<string, Point3D>(), new List<(Point3D, Point3D)>());
        var window = new DeviceWindow { DeviceId = "phone", Start = DateTime.UtcNow, End = DateTime.UtcNow, RefRssi = -59, Readings = Array.Empty<DeviceWindowReading>() };
        var snapshots = Enumerable.Range(0, 6).Select(i => new OptimizationSnapshot
        {
            Timestamp = DateTime.UtcNow.AddSeconds(30 * i),
            DeviceWindows = new List<DeviceWindow> { window },
            Geometry = geometry,
        }).ToList();

        Assert.That(OptimizationDataSplit.TryCreate(snapshots, 0.2, out var split), Is.True);

        Assert.That(split!.Training.DeviceWindows, Has.Count.EqualTo(4));
        Assert.That(split.Training.Geometry, Is.SameAs(geometry));
        Assert.That(split.Validation.Select(v => v.DeviceWindows.Count), Is.All.EqualTo(1));
    }
}
