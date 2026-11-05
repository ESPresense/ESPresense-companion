using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The device_residual optimizer on a synthetic two-floor house whose truth is known: twelve nodes, devices
/// on random walks heard with ~3 dB of noise, one node that hears devices 10 dB hot and one device that
/// transmits 8 dB quieter than its rssi@1m says. Readings are windowed by <see cref="DeviceResidualStore"/>.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceResidualOptimizerTests
{
    private const double Absorption = DeviceResidualModel.DefaultAbsorption;
    private const double AdvertisedRef = -59;

    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));
        await _configLoader.LoadAsync();
        Assert.That(_state.Config?.Optimization, Is.Not.Null, "config did not load");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    internal static DeviceGeometry Geometry() => new(
        DeviceGridSolverTests.House.ToDictionary(n => n.Id, n => new Point3D(n.X, n.Y, n.Z), StringComparer.OrdinalIgnoreCase),
        DeviceGridSolverTests.Floors);

    private static double Gauss(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>A simulated device: where it is over time and how loud it really is.</summary>
    internal sealed record SimDevice(string Id, double TrueRef, Func<int, Point3D> At);

    /// <summary>A device wandering one floor, about half a metre per window, changing floor now and then.</summary>
    internal static SimDevice Walker(string id, int seed, double trueRef = AdvertisedRef)
    {
        var rnd = new Random(seed);
        var path = new List<Point3D>();
        double x = 2 + rnd.NextDouble() * 12, y = 2 + rnd.NextDouble() * 8;
        var floor = rnd.Next(2);
        for (int i = 0; i < 4000; i++)
        {
            x = Math.Clamp(x + 0.5 * Gauss(rnd), 0.5, 15.5);
            y = Math.Clamp(y + 0.5 * Gauss(rnd), 0.5, 11.5);
            if (rnd.NextDouble() < 0.01) floor = 1 - floor;
            path.Add(new Point3D(x, y, floor * 3 + 0.8 + 0.4 * rnd.NextDouble()));
        }
        return new SimDevice(id, trueRef, i => path[i]);
    }

    /// <summary>
    /// Feeds <paramref name="windows"/> windows of every device into a store, three readings per node per
    /// window, each rssi = trueRef + nodeBias - 10 a log10 d + linkBias + noise, published with rxAdj 0.
    /// </summary>
    internal static List<DeviceWindow> Windows(IEnumerable<SimDevice> devices, int windows, Func<string, double> nodeBias,
        Func<string, string, double>? linkBias = null, double noiseSd = 3, int seed = 7)
    {
        var rnd = new Random(seed);
        var store = new DeviceResidualStore();
        var settings = new ConfigDeviceResidual();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var device in devices)
            for (int i = 0; i < windows; i++)
            {
                var p = device.At(i);
                foreach (var n in DeviceGridSolverTests.House)
                {
                    var d = Math.Max(p.DistanceTo(new Point3D(n.X, n.Y, n.Z)), DeviceGridSolver.MinDistance);
                    var mean = device.TrueRef + nodeBias(n.Id) + (linkBias?.Invoke(device.Id, n.Id) ?? 0) - 10 * Absorption * Math.Log10(d);
                    for (int k = 0; k < 3; k++)
                        store.Add(device.Id, t0.AddSeconds(i * 10 + 1 + 3 * k), n.Id, mean + noiseSd * Gauss(rnd), 0, AdvertisedRef, settings);
                }
            }
        return store.Windows(null, null, t0.AddSeconds(windows * 10 + 60));
    }

    private OptimizationSnapshot Snapshot(List<DeviceWindow> windows) => new() { DeviceWindows = windows, Geometry = Geometry() };

    /// <summary>Runs <paramref name="cycles"/> cycles, applying each proposal's rxAdj the way the runner would.</summary>
    private (Dictionary<string, double> RxAdj, OptimizationResults Last) Cycles(DeviceResidualOptimizer optimizer, OptimizationSnapshot os, int cycles)
    {
        var rxAdj = DeviceGridSolverTests.House.ToDictionary(n => n.Id, _ => 0.0);
        OptimizationResults last = new();
        for (int c = 0; c < cycles; c++)
        {
            var settings = rxAdj.ToDictionary(p => p.Key, p => new NodeSettings(p.Key) { Calibration = { RxAdjRssi = (int)Math.Round(p.Value) } });
            last = optimizer.Optimize(os, settings).QuantizeForApplication();
            foreach (var (id, pv) in last.Nodes)
                if (pv.RxAdjRssi is { } v) rxAdj[id] = v;
        }
        return (rxAdj, last);
    }

    private DeviceResidualOptimizer NewOptimizer() => new(_state, NullLogger<DeviceResidualOptimizer>.Instance);

    [Test]
    public void RecoversANodesDeviceFacingBias_AndADevicesTransmitOffset()
    {
        var devices = Enumerable.Range(0, 6).Select(i => Walker($"walker{i}", 100 + i, i == 2 ? AdvertisedRef - 8 : AdvertisedRef)).ToList();
        var windows = Windows(devices, 300, id => id == "n03" ? 10 : 0);
        var optimizer = NewOptimizer();

        var (rxAdj, last) = Cycles(optimizer, Snapshot(windows), 4);

        // Offsets are relative: rxAdj changes are centred, so the absolute level goes to the devices' rssi@1m.
        var othersRx = rxAdj.Where(p => p.Key != "n03").Average(p => p.Value);
        var nodeBias = rxAdj["n03"] - othersRx;
        var otherRefs = last.Devices.Where(p => p.Key != "walker2").Average(p => p.Value.RefRssi);
        var deviceOffset = last.Devices["walker2"].RefRssi - otherRefs;
        TestContext.Out.WriteLine($"node bias recovered {nodeBias:+0.00;-0.00} dB (truth +10); device offset {deviceOffset:+0.00;-0.00} dB (truth -8)");
        TestContext.Out.WriteLine("rxAdj: " + string.Join(", ", rxAdj.Select(p => $"{p.Key}={p.Value:0}")));
        TestContext.Out.WriteLine("rssi@1m: " + string.Join(", ", last.Devices.Select(p => $"{p.Key}={p.Value.RefRssi:0.0} (mobility {p.Value.Mobility:0.00})")));
        var state = last.DeviceResidual!;
        TestContext.Out.WriteLine($"last cycle: |LOO residual| {state.ResidualBefore:0.00} -> {state.ResidualAfter:0.00} dB, scale {state.ScaleBefore:0.000} -> {state.ScaleAfter:0.000}");

        Assert.That(nodeBias, Is.EqualTo(10).Within(1.5));
        Assert.That(deviceOffset, Is.EqualTo(-8).Within(1.0));
        foreach (var (id, v) in rxAdj.Where(p => p.Key != "n03"))
            Assert.That(v - othersRx, Is.EqualTo(0).Within(1.5), $"{id} should not move relative to the other unbiased nodes");
        Assert.That(last.Devices.Values.All(d => d.Mobility > 0.5), Is.True, "random walkers are mobile");
    }

    /// <summary>
    /// A device that never moves sits 1.5 m from n05 with its link to n05 10 dB hot (a reflection, or a shelf in
    /// the way): a residual that is real but belongs to that spot, not to the node. Its windows get low mobility
    /// weight, so n05's proposal is the same with or without it. Weighted equally with the four walkers it would
    /// have pulled n05 by about 2 dB.
    /// </summary>
    [Test]
    public void StationaryDevice_HasLowMobility_AndDoesNotShiftANode()
    {
        var spot = new Point3D(8.2, 10.1, 0.9);
        var walkers = Enumerable.Range(0, 4).Select(i => Walker($"walker{i}", 200 + i)).ToList();
        var tv = new SimDevice("tv", AdvertisedRef, _ => spot);
        Func<string, string, double> reflection = (device, node) => device == "tv" && node == "n05" ? 10 : 0;

        double Relative(OptimizationResults r, string id) => r.Nodes[id].RxAdjRssi!.Value - r.Nodes.Values.Average(v => v.RxAdjRssi!.Value);
        var without = NewOptimizer().Optimize(Snapshot(Windows(walkers, 300, _ => 0)), new Dictionary<string, NodeSettings>());
        var with = NewOptimizer().Optimize(Snapshot(Windows(walkers.Append(tv), 300, _ => 0, reflection)), new Dictionary<string, NodeSettings>());

        var shift = Relative(with, "n05") - Relative(without, "n05");
        TestContext.Out.WriteLine($"tv mobility {with.Devices["tv"].Mobility:0.00}; n05 shifted {shift:+0.00;-0.00} dB by the still device");
        Assert.That(with.Devices["tv"].Mobility, Is.LessThan(0.1));
        Assert.That(with.Devices.Where(p => p.Key != "tv").All(p => p.Value.Mobility > 0.5), Is.True);
        Assert.That(Math.Abs(shift), Is.LessThan(0.5), "a still device's residual must not move the node");
    }

    [Test]
    public void OnlyStationaryDevices_MoveNoNode()
    {
        var devices = new[]
        {
            new SimDevice("tv", AdvertisedRef, _ => new Point3D(8.2, 10.1, 0.9)),
            new SimDevice("speaker", AdvertisedRef, _ => new Point3D(3.0, 2.0, 4.0)),
        };
        var windows = Windows(devices, 300, id => id == "n03" ? 10 : 0);

        var results = NewOptimizer().Optimize(Snapshot(windows), new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Empty, "devices that do not move cannot tell a node's offset from their own position");
        Assert.That(results.Devices.Values.All(d => d.Mobility < DeviceResidualOptimizer.MovingMobility), Is.True);
        Assert.That(results.DeviceResidual!.Nodes.Values.All(n => n.RxAdjChange == null), Is.True);
        Assert.That(_state.OptimizerState.DeviceResidual, Is.Null, "the optimizer reports on its results; only the live runner publishes to State (a dry run must change nothing)");
    }

    [Test]
    public void NoDeviceWindows_ProposesNothing()
    {
        var results = NewOptimizer().Optimize(new OptimizationSnapshot(), new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Empty);
        Assert.That(results.Devices, Is.Empty);
    }

    [Test]
    public void Fork_StartsFromTheLearnedState_WithoutAdvancingTheOriginal()
    {
        var devices = Enumerable.Range(0, 4).Select(i => Walker($"walker{i}", 300 + i, i == 1 ? AdvertisedRef + 6 : AdvertisedRef)).ToList();
        var os = Snapshot(Windows(devices, 200, _ => 0));
        var live = NewOptimizer();
        for (int c = 0; c < 2; c++) live.Optimize(os, new Dictionary<string, NodeSettings>());

        var forked = live.Fork().Optimize(os, new Dictionary<string, NodeSettings>()).Devices["walker1"].StableCycles;
        var next = live.Optimize(os, new Dictionary<string, NodeSettings>()).Devices["walker1"].StableCycles;

        Assert.That(forked, Is.GreaterThan(0), "the fork carries the learned rssi@1m and its stable count");
        Assert.That(next, Is.EqualTo(forked), "running the fork did not advance the original");
    }

    [Test]
    public void LearnedReference_BecomesStableAfterItStopsMoving()
    {
        var devices = Enumerable.Range(0, 4).Select(i => Walker($"walker{i}", 300 + i, i == 1 ? AdvertisedRef + 6 : AdvertisedRef)).ToList();
        var windows = Windows(devices, 200, _ => 0);
        var optimizer = NewOptimizer();
        var os = Snapshot(windows);

        var stable = new List<int>();
        for (int c = 0; c < 4; c++)
            stable.Add(optimizer.Optimize(os, new Dictionary<string, NodeSettings>()).Devices["walker1"].StableCycles);

        TestContext.Out.WriteLine("walker1 stable cycles: " + string.Join(", ", stable));
        Assert.That(stable[0], Is.EqualTo(0), "the first cycle has nothing to compare with");
        Assert.That(stable[^1], Is.GreaterThanOrEqualTo(2));
    }
}
