using System.Globalization;
using System.Text.Json;
using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The device_residual optimizer on an evening of real traffic, when it is on this machine (it is house data,
/// so it is never committed): readings.tsv (t, device, node, rawRssi, rssi, rssi@1m per reading) and the
/// config.json of node positions and floors. Readings go through <see cref="DeviceResidualStore"/> as the
/// locator would feed them; node positions are the only ground truth. The learned node offsets are compared
/// with whichever reference offsets are present: expected-offsets.json, else a robot survey's rx_adj plan.
/// Skipped when the data is absent, as on CI. Explicit because it solves tens of thousands of windows (a minute
/// or more): run it with <c>dotnet test --filter TestCategory=RealData</c>.
/// </summary>
[Category("RealData")]
[Explicit("an evening of house data; run with --filter TestCategory=RealData")]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceResidualRealDataTests
{
    private const string Readings = "/tmp/replay-eval/readings.tsv";
    private const string ConfigJson = "/tmp/replay-eval/config.json";
    private static readonly string[] References = { "/tmp/optimizer-ref/expected-offsets.json", "/tmp/replay-eval/robot-cal-plan.json" };

    /// <summary>The reference implementation's absorption, used for every node.</summary>
    private const double Absorption = 3.0;

    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!File.Exists(Readings) || !File.Exists(ConfigJson)) Assert.Ignore("real readings are not on this machine");
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        await _configLoader.LoadAsync();
        // Offsets are learned from raw rssi (rxAdj 0), so the limits must not clip them.
        _state.Config!.Optimization.Limits["rx_adj_rssi_min"] = -30;
        _state.Config!.Optimization.Limits["rx_adj_rssi_max"] = 30;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_configLoader == null) return;
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private static DeviceGeometry LoadGeometry(out Dictionary<string, string> names)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigJson));
        var nodes = new Dictionary<string, Point3D>(StringComparer.OrdinalIgnoreCase);
        names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in doc.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (!n.TryGetProperty("point", out var point) || point.ValueKind != JsonValueKind.Array) continue;
            var p = point.EnumerateArray().Select(v => v.GetDouble()).ToArray();
            var id = n.GetProperty("id").GetString()!;
            nodes[id] = new Point3D(p[0], p[1], p.Length > 2 ? p[2] : 0);
            names[id] = n.TryGetProperty("name", out var name) ? name.GetString() ?? id : id;
        }
        var floors = doc.RootElement.GetProperty("floors").EnumerateArray()
            .Where(f => f.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Array)
            .Select(f =>
            {
                var b = f.GetProperty("bounds").EnumerateArray().Select(c => c.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
                return (Min: new Point3D(b[0][0], b[0][1], b[0][2]), Max: new Point3D(b[1][0], b[1][1], b[1][2]));
            })
            .ToList();
        return new DeviceGeometry(nodes, floors);
    }

    /// <summary>Reads every reading through the store. Transmitting nodes (node:*) are left out: configured ones are node-to-node data, not devices, and the reference held out the one survey tag.</summary>
    private static (List<DeviceWindow> Training, List<DeviceWindow> HeldOut) LoadWindows(DeviceGeometry geometry)
    {
        var store = new DeviceResidualStore();
        var heldOut = new DeviceResidualStore();
        var settings = new ConfigDeviceResidual();
        var last = DateTime.MinValue;
        foreach (var line in File.ReadLines(Readings))
        {
            var f = line.Split('\t');
            if (f.Length < 6 || !geometry.Nodes.ContainsKey(f[2])) continue;
            var t = DateTime.UnixEpoch.AddSeconds(double.Parse(f[0], CultureInfo.InvariantCulture));
            var raw = double.Parse(f[3], CultureInfo.InvariantCulture);
            var rssi = double.Parse(f[4], CultureInfo.InvariantCulture);
            var reference = double.Parse(f[5], CultureInfo.InvariantCulture);
            (f[1].StartsWith("node:", StringComparison.Ordinal) ? heldOut : store).Add(f[1], t, f[2], rssi, raw - rssi, reference, settings);
            if (t > last) last = t;
        }
        // History long enough to keep the whole evening.
        return (store.Windows(null, null, last.AddMinutes(1)), heldOut.Windows(null, null, last.AddMinutes(1)));
    }

    private static Dictionary<string, double>? LoadReference(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement.TryGetProperty("rx_adj", out var rx) ? rx : doc.RootElement;
        return root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number)
            .ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.OrdinalIgnoreCase);
    }

    private static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Count; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    [Test]
    public void LearnedNodeOffsets_AgreeWithTheReference()
    {
        var geometry = LoadGeometry(out var names);
        var (windows, heldOut) = LoadWindows(geometry);
        TestContext.Out.WriteLine($"{windows.Count} windows from {windows.Select(w => w.DeviceId).Distinct().Count()} devices; {heldOut.Count} held-out windows");
        Assert.That(windows.Count, Is.GreaterThan(1000));

        var optimizer = new DeviceResidualOptimizer(_state, NullLogger<DeviceResidualOptimizer>.Instance);
        var os = new OptimizationSnapshot { DeviceWindows = windows, Geometry = geometry };
        var rxAdj = geometry.Nodes.Keys.ToDictionary(id => id, _ => 0.0, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, NodeSettings> Settings() => rxAdj.ToDictionary(p => p.Key,
            p => new NodeSettings(p.Key) { Calibration = { RxAdjRssi = (int)Math.Round(p.Value), Absorption = Absorption } });

        OptimizationResults results = new();
        for (int cycle = 0; cycle < 4; cycle++)
        {
            results = optimizer.Optimize(os, Settings()).QuantizeForApplication();
            foreach (var (id, pv) in results.Nodes)
                if (pv.RxAdjRssi is { } v) rxAdj[id] = v;
            var s = results.DeviceResidual!;
            TestContext.Out.WriteLine($"cycle {cycle + 1}: {s.Windows} windows, {s.Devices} devices ({s.MovingDevices} moving); |LOO residual| {s.ResidualBefore:0.00} -> {s.ResidualAfter:0.00} dB; scale {s.ScaleBefore:0.000} -> {s.ScaleAfter:0.000}");
        }

        var mean = rxAdj.Values.Average();
        var learned = rxAdj.ToDictionary(p => p.Key, p => p.Value - mean, StringComparer.OrdinalIgnoreCase);
        var trust = results.DeviceResidual!.Nodes;
        foreach (var (id, v) in learned.OrderByDescending(p => p.Value))
            TestContext.Out.WriteLine($"  {names[id],-18} learned {v:+0.0;-0.0} dB  var {trust.GetValueOrDefault(id)?.Variance ?? double.NaN,5:0.0}  windows {trust.GetValueOrDefault(id)?.Windows ?? 0}");
        foreach (var (id, d) in results.Devices.OrderByDescending(p => p.Value.Windows).Take(12))
            TestContext.Out.WriteLine($"  device {Math.Abs(id.GetHashCode()) % 1000:000}  rssi@1m {d.RefRssi:0.0} (ranged with {d.CurrentRefRssi:0})  mobility {d.Mobility:0.00}  windows {d.Windows}");

        // The held-out transmitter's own rssi@1m, learned from its own residuals with the learned node offsets.
        if (heldOut.Count > 0)
        {
            var tag = new DeviceResidualOptimizer(_state, NullLogger<DeviceResidualOptimizer>.Instance);
            var tagOs = new OptimizationSnapshot { DeviceWindows = heldOut, Geometry = geometry };
            OptimizationResults tagResults = new();
            for (int cycle = 0; cycle < 3; cycle++) tagResults = tag.Optimize(tagOs, Settings());
            foreach (var (_, d) in tagResults.Devices)
                TestContext.Out.WriteLine($"  held-out transmitter: learned rssi@1m {d.RefRssi:0.0} (ranged with {d.CurrentRefRssi:0}), {d.Windows} windows, mobility {d.Mobility:0.00}");
        }

        var compared = false;
        foreach (var path in References)
        {
            var reference = LoadReference(path);
            if (reference == null) continue;
            var ids = learned.Keys.Where(reference.ContainsKey).ToList();
            if (ids.Count < 5) continue;
            var refMean = ids.Average(id => reference[id]);
            var x = ids.Select(id => learned[id]).ToList();
            var y = ids.Select(id => reference[id] - refMean).ToList();
            var r = Pearson(x, y);
            var rms = Math.Sqrt(ids.Average(id => Math.Pow(learned[id] - (reference[id] - refMean), 2)));
            TestContext.Out.WriteLine($"{Path.GetFileName(path)}: r = {r:0.00}, rms difference {rms:0.0} dB over {ids.Count} nodes");
            Assert.That(r, Is.GreaterThan(0.5), $"learned offsets should agree with {Path.GetFileName(path)}");
            compared = true;
        }
        if (!compared) Assert.Ignore("no reference offsets to compare with");
    }
}
