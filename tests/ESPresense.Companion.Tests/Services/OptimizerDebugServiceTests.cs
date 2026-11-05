using System.Text.Json;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// The optimizer debugging tools (#1857) on a fabricated history: three nodes in a full mesh whose RSSI follows an
/// absorption of exactly 3.0 from a txRef of -59, so the incumbent 2.7 default is wrong in a known way. All ids are made up.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OptimizerDebugServiceTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private ConfigLoader _configLoader = null!;
    private State _state = null!;
    private NodeSettingsStore _nss = null!;
    private OptimizationRunner _runner = null!;
    private OptimizerDebugService _service = null!;
    private string _configDir = null!;

    private static readonly (string Id, Point3D At)[] Nodes =
    {
        ("node-a", new Point3D(0, 0, 1)),
        ("node-b", new Point3D(4, 0, 1)),
        ("node-c", new Point3D(0, 3, 1)),
    };

    [SetUp]
    public async Task Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));
        await _configLoader.LoadAsync();
        _configLoader.Config!.Optimization.TrainingWindowMins = 30; // the example config's 60 would need 30 minutes of history
        _nss = new NodeSettingsStore(_mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        _runner = new OptimizationRunner(_state, _nss, NullLogger<OptimizationRunner>.Instance, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance);
        _service = new OptimizerDebugService(_runner, _state);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }

    private static OptimizationSnapshot MeshSnapshot(DateTime at)
    {
        var nodes = Nodes.ToDictionary(n => n.Id, n => new OptNode { Id = n.Id, Name = n.Id.ToUpperInvariant(), Location = n.At });
        var os = new OptimizationSnapshot { Timestamp = at };
        foreach (var tx in nodes.Values)
            foreach (var rx in nodes.Values.Where(r => r != tx))
            {
                var d = tx.Location.DistanceTo(rx.Location);
                os.Measures.Add(new Measure { Tx = tx, Rx = rx, Rssi = -59 - 30 * Math.Log10(d), RssiRxAdj = 0, RefRssi = -59, Distance = d });
            }
        return os;
    }

    /// <summary>31 snapshots 30 s apart: 15 minutes, half the default 30-minute window, so a scheduled cycle runs.</summary>
    private void AddHistory(int count = 31)
    {
        var start = DateTime.UtcNow - TimeSpan.FromSeconds(30) * (count - 1);
        _runner.Snapshots.AddRange(Enumerable.Range(0, count).Select(i => MeshSnapshot(start + TimeSpan.FromSeconds(30) * i)));
    }

    private static ConfigOptimization Optimization => new() { Enabled = true, TrainingWindowMins = 30 };

    private sealed class FixedAbsorption : IOptimizer
    {
        public string Name => "Fixed";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            foreach (var (id, _) in Nodes) results.Nodes[id] = new ProposedValues { Absorption = 3.0 };
            return results;
        }
    }

    /// <summary>The right absorption plus a txRef/rxAdj shift that cancels node-to-node: what the device-frame gate is for.</summary>
    private sealed class GaugeShifted : IOptimizer
    {
        public string Name => "GaugeShifted";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            foreach (var (id, _) in Nodes) results.Nodes[id] = new ProposedValues { Absorption = 3.0, RxAdjRssi = -10, TxRefRssi = -49 };
            return results;
        }
    }

    [Test]
    public void LastCycle_IsNullBeforeAnyCycle()
    {
        Assert.That(_service.GetLastCycle(), Is.Null);
        Assert.That(new McpOptimizerTools(_service).GetOptimizerLastCycle(), Does.Contain("\"ok\":false"));
    }

    [Test]
    public async Task LiveCycle_RecordsEveryProposalWithItsGateDecision()
    {
        AddHistory();

        Assert.That(await _runner.RunScheduledCycleAsync(Optimization, new List<IOptimizer> { new GaugeShifted(), new FixedAbsorption() }), Is.True);

        var report = _service.GetLastCycle()!;
        Assert.That(report, Is.Not.Null);
        Assert.That(report.DryRun, Is.False);
        Assert.That(report.EvalSnapshots, Is.EqualTo(7), "20% of 31 held out");
        Assert.That(report.TrainingMeasures, Is.EqualTo(24 * 6));
        Assert.That(report.Baseline.Samples, Is.EqualTo(7 * 6));
        Assert.That(report.Baseline.Rmse, Is.GreaterThan(0), "the 2.7 default does not fit an absorption of 3.0");

        var gauge = report.Proposals.Single(p => p.Optimizer == "GaugeShifted");
        Assert.That(gauge.Decision, Is.EqualTo(OptimizerGateDecision.DeviceFrame));
        Assert.That(gauge.Applied, Is.False);
        Assert.That(gauge.Gates.Improvement.Passed, Is.True, "it wins node-to-node");
        Assert.That(gauge.Gates.DeviceFrame, Is.Not.Null);
        Assert.That(gauge.Gates.DeviceFrame!.Passed, Is.False);
        Assert.That(gauge.Gates.DeviceFrame.Composite, Is.LessThanOrEqualTo(report.Baseline.Composite));
        Assert.That(gauge.Nodes["node-a"], Is.EqualTo(new OptimizerNodeProposal(3.0, -10, -49, null)));

        var fixedAbs = report.Proposals.Single(p => p.Optimizer == "Fixed");
        Assert.That(fixedAbs.Decision, Is.EqualTo(OptimizerGateDecision.Selected));
        Assert.That(fixedAbs.Applied, Is.True);
        Assert.That(fixedAbs.Score.Rmse, Is.EqualTo(0).Within(1e-9));
        Assert.That(fixedAbs.Gates.MinSamples, Is.EqualTo(new OptimizerMinSamplesGate(42, 5, true)));
        Assert.That(fixedAbs.Gates.DeviceFrame, Is.Null, "it leaves txRef alone");
        Assert.That(fixedAbs.Gates.EverySnapshot, Is.EqualTo(new OptimizerEverySnapshotGate(0, 7, true)));

        Assert.That(report.Selected, Is.EqualTo("Fixed"));
        Assert.That(report.Applied, Is.True);
        Assert.That(report.Outcome, Is.EqualTo(_state.OptimizerState.LastOutcome));
        Assert.That(_nss.Get("node-a").Calibration.Absorption, Is.EqualTo(3.0), "the live cycle applied it");
    }

    [Test]
    public async Task DryRun_ReportsWithoutApplyingOrPublishing()
    {
        AddHistory();
        var outcomeBefore = _state.OptimizerState.LastOutcome;

        var report = await _service.RunDryCycleAsync();

        Assert.That(report.DryRun, Is.True);
        Assert.That(report.Proposals.Select(p => p.Optimizer), Is.EqualTo(_runner.OptimizerNames), "the configured set");
        Assert.That(report.Selected, Is.Not.Null, "something passes the gate, so a live cycle would have applied it");
        Assert.That(report.Applied, Is.False);
        Assert.That(report.Proposals.Any(p => p.Applied), Is.False);
        Assert.That(report.Outcome, Does.StartWith("Would apply"));

        Assert.That(_mqtt.Invocations, Is.Empty, "a dry run must never reach NodeSettingsStore.Set or MQTT");
        foreach (var (id, _) in Nodes)
            Assert.That(_nss.Get(id).Calibration.Absorption, Is.Null);
        Assert.That(_service.GetLastCycle(), Is.Null, "a dry run is not the last live cycle");
        Assert.That(_state.OptimizerState.LastOutcome, Is.EqualTo(outcomeBefore));
    }

    [Test]
    public async Task DryRun_OneOptimizerByName_AnyMode()
    {
        AddHistory();
        Assume.That(_runner.OptimizerNames, Does.Not.Contain("Absorption Avg"), "the point is an optimizer the configured mode does not run");

        var report = await _service.RunDryCycleAsync("absorption avg");

        Assert.That(report.Proposals.Select(p => p.Optimizer), Is.EqualTo(new[] { "Absorption Avg" }));
        Assert.That(_mqtt.Invocations, Is.Empty);
    }

    [Test]
    public void DryRun_UnknownOptimizer_ListsTheKnownOnes()
    {
        AddHistory();

        var ex = Assert.ThrowsAsync<OptimizerUnavailableException>(() => _service.RunDryCycleAsync("nope"));
        Assert.That(ex!.Message, Does.Contain("Absorption Avg"));
    }

    [Test]
    public async Task DryRun_TooLittleHistory_IsRefusedAsJson()
    {
        AddHistory(3);

        var json = JsonDocument.Parse(await new McpOptimizerTools(_service).RunOptimizerDryCycle()).RootElement;

        Assert.That(json.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("error").GetString(), Does.Contain("3 snapshots"));
    }

    [Test]
    public async Task DryRun_ShortHistory_RunsButSaysTheScheduledCycleWouldNot()
    {
        AddHistory(6); // splittable, but 2.5 minutes of a 30-minute window

        var report = await _service.RunDryCycleAsync();

        Assert.That(report.Note, Does.Contain("would not run"));
    }

    /// <summary>
    /// Hand-computed: one receiver at the origin hears transmitters at 2 m (-68) and 4 m (-75). The incumbent defaults
    /// (txRef -59, absorption 2.7) predict -59 - 27 log10(d) = -67.128 and -75.256, residuals -0.872 and +0.256;
    /// a proposal of absorption 3.0 predicts -68.031 and -77.062, residuals +0.031 and +2.062.
    /// </summary>
    [Test]
    public async Task Residuals_MatchAHandComputedValue()
    {
        var rx = new OptNode { Id = "rx", Name = "rx", Location = new Point3D(0, 0, 0) };
        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(new Measure { Rx = rx, Tx = new OptNode { Id = "tx0", Location = new Point3D(2, 0, 0) }, Rssi = -68 });
        snapshot.Measures.Add(new Measure { Rx = rx, Tx = new OptNode { Id = "tx1", Location = new Point3D(4, 0, 0) }, Rssi = -75 });

        await _runner.RunCycleAsync(snapshot, new List<OptimizationSnapshot> { snapshot },
            new ConfigOptimization { Enabled = true, MinSamples = 2 }, new List<IOptimizer> { new RxAbsorption3() });

        var residuals = (await _service.GetResidualsAsync())!;
        Assert.That(residuals.Scenarios, Is.EqualTo(new[] { "incumbent", "Rx3" }));

        var rxRes = residuals.Nodes.Single(n => n.Id == "rx");
        Assert.That(rxRes.AsTx, Is.Empty, "rx never transmits");
        AssertStats(rxRes.AsRx["incumbent"], 2, -0.30829, 0.64267);
        AssertStats(rxRes.AsRx["Rx3"], 2, 1.04635, 1.45808);

        var tx0 = residuals.Nodes.Single(n => n.Id == "tx0");
        Assert.That(tx0.AsRx, Is.Empty);
        AssertStats(tx0.AsTx["incumbent"], 1, -0.87219, 0.87219);
        AssertStats(tx0.AsTx["Rx3"], 1, 0.03090, 0.03090);
    }

    private static void AssertStats(OptimizerResidualStats s, int n, double mean, double rms)
    {
        Assert.That(s.N, Is.EqualTo(n));
        Assert.That(s.Mean, Is.EqualTo(mean).Within(1e-4));
        Assert.That(s.Rms, Is.EqualTo(rms).Within(1e-4));
    }

    private sealed class RxAbsorption3 : IOptimizer
    {
        public string Name => "Rx3";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
            => new() { Nodes = { ["rx"] = new ProposedValues { Absorption = 3.0 } } };
    }

    [Test]
    public async Task Residuals_ProposalThatMovesTxRef_IsAlsoReportedWithTxRefHeld()
    {
        AddHistory();
        await _runner.RunScheduledCycleAsync(Optimization, new List<IOptimizer> { new GaugeShifted() });

        var residuals = (await _service.GetResidualsAsync())!;

        Assert.That(residuals.Scenarios, Is.EqualTo(new[] { "incumbent", "GaugeShifted", "GaugeShifted (txRef held)" }));
        var a = residuals.Nodes.Single(n => n.Id == "node-a");
        Assert.That(a.AsRx["GaugeShifted"].Rms, Is.EqualTo(0).Within(1e-9), "the shift cancels node-to-node");
        Assert.That(a.AsRx["GaugeShifted (txRef held)"].Mean, Is.EqualTo(10).Within(1e-9), "a 10 dB range bias for every device");
    }

    [Test]
    public void Measures_PoolTheTrainingSnapshotIntoCells()
    {
        var anchor = new OptNode { Id = "anchor-1", Name = "Anchor", Location = new Point3D(2, 0, 1), IsAnchor = true };
        var rx = new OptNode { Id = "node-a", Name = "A", Location = new Point3D(0, 0, 1) };
        var start = DateTime.UtcNow.AddMinutes(-10);
        _runner.Snapshots.AddRange(Enumerable.Range(0, 5).Select(i => new OptimizationSnapshot
        {
            Timestamp = start.AddMinutes(i),
            Measures =
            {
                // rxAdj changed between snapshots, as it does when a live cycle applies mid-window.
                new Measure { Tx = anchor, Rx = rx, Rssi = -60 - i, RssiRxAdj = i < 2 ? 0 : 3, RefRssi = -59, RssiVar = 1, Distance = 2.5 }
            }
        }));

        var report = _service.GetMeasures();

        // 5 snapshots, max(2, ceil(5 * 0.2)) = 2 held out, 3 pooled for training.
        Assert.That(report.Available, Is.True);
        Assert.That(report.TrainingSnapshots, Is.EqualTo(3));
        Assert.That(report.Names, Is.EqualTo(new Dictionary<string, string?> { ["anchor-1"] = "Anchor", ["node-a"] = "A" }));
        var cell = report.Cells.Single();
        Assert.That(cell.Tx, Is.EqualTo("anchor-1"));
        Assert.That(cell.TxAnchor, Is.True);
        Assert.That(cell.Samples, Is.EqualTo(3));
        Assert.That(cell.Rssi, Is.EqualTo(-61));
        Assert.That(cell.RssiSd, Is.EqualTo(1));
        Assert.That(cell.RxAdj, Is.EqualTo(new double?[] { 0, 3 }));
        Assert.That(cell.MapDistance, Is.EqualTo(2));
        Assert.That(cell.Distance, Is.EqualTo(2.5));
        Assert.That(_service.GetMeasures("node-b").Cells, Is.Empty);
    }

    [Test]
    public void Measures_TooLittleHistory_SaysWhy()
    {
        var report = _service.GetMeasures();
        Assert.That(report.Available, Is.False);
        Assert.That(report.Reason, Does.Contain("0 snapshots"));
    }

    [Test]
    public void Status_DescribesTheHistoryAndSplit()
    {
        AddHistory();

        var status = _service.GetStatus();

        Assert.That(status.Mode, Is.EqualTo(_state.Config!.Optimization.Optimizer.ToLowerInvariant()));
        Assert.That(status.Optimizers, Is.EqualTo(_runner.OptimizerNames).And.Not.Empty);
        Assert.That(status.SnapshotCount, Is.EqualTo(31));
        Assert.That(status.MeasurementCount, Is.EqualTo(31 * 6));
        Assert.That(status.SpanMins, Is.EqualTo(15).Within(0.01));
        Assert.That(status.SufficientHistory, Is.True);
        Assert.That(status.Split, Is.EqualTo(new OptimizerSplitSizes(24, 24 * 6, 7, 7 * 6)));
        Assert.That(status.Settings.MinSamples, Is.EqualTo(5));
    }

    [Test]
    public async Task Tools_ReturnTheRestJson()
    {
        AddHistory();
        await _runner.RunScheduledCycleAsync(Optimization, new List<IOptimizer> { new FixedAbsorption() });
        var tools = new McpOptimizerTools(_service);
        var controller = new OptimizerController(_service);
        string Rest(object? value) => JsonSerializer.Serialize(value, JsonDefaults.Web);

        Assert.That(tools.GetOptimizerLastCycle(), Is.EqualTo(Rest(controller.GetLastCycle().Value)));
        Assert.That(await tools.GetOptimizerResiduals(), Is.EqualTo(Rest((await controller.GetResiduals()).Value)));
        Assert.That(tools.GetOptimizerMeasures(), Is.EqualTo(Rest(controller.GetMeasures())));

        var json = JsonDocument.Parse(tools.GetOptimizerLastCycle()).RootElement;
        var proposal = json.GetProperty("proposals")[0];
        Assert.That(proposal.GetProperty("decision").GetString(), Is.EqualTo("Selected"), "enums as strings, as REST");
        Assert.That(proposal.GetProperty("nodes").GetProperty("node-a").GetProperty("absorption").GetDouble(), Is.EqualTo(3.0));
        Assert.That(proposal.GetProperty("nodes").GetProperty("node-a").TryGetProperty("txRefRssi", out _), Is.False, "unchanged values are left out");
        Assert.That(proposal.GetProperty("gates").GetProperty("deviceFrame").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public void Controller_LastCycleBeforeAnyCycle_IsNotFound()
    {
        Assert.That(new OptimizerController(_service).GetLastCycle().Result, Is.TypeOf<NotFoundObjectResult>());
    }

    [Test]
    public void Tools_AreRegisteredReadOnlyUnderTheirNames()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_service);
        services.AddMcpServer().WithTools<McpOptimizerTools>();
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToList();

        Assert.That(tools.Select(t => t.ProtocolTool.Name), Is.EquivalentTo(new[]
        {
            "get_optimizer_status", "get_optimizer_last_cycle", "get_optimizer_residuals", "get_optimizer_measures", "run_optimizer_dry_cycle"
        }));
        Assert.That(tools.All(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true), Is.True);
        var dryRun = tools.Single(t => t.ProtocolTool.Name == "run_optimizer_dry_cycle").ProtocolTool.InputSchema;
        Assert.That(dryRun.GetProperty("properties").TryGetProperty("optimizer", out _), Is.True);
    }

    [Test]
    public void ReadMeasures_MarksAnchoredDevicesAsAnchors()
    {
        var node = new Node("node-a", NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Name = "A", Point = new[] { 0.0, 0, 1 } }, Enumerable.Empty<Floor>());
        _state.Nodes[node.Id] = node;
        var device = new Device("anchor-1", null, TimeSpan.FromSeconds(30)) { Name = "Anchor" };
        device.SetAnchor(new DeviceAnchor(new Point3D(2, 0, 1), null, null));
        device.Nodes[node.Id] = new DeviceToNode(device, node) { Distance = 2, Rssi = -60, LastHit = DateTime.UtcNow };
        _state.Devices[device.Id] = device;

        var m = _state.ReadMeasures().Measures.Single();

        Assert.That(m.Tx.IsAnchor, Is.True);
        Assert.That(m.Rx.IsAnchor, Is.False);
    }
}
