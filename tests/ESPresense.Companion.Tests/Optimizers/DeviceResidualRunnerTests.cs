using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using static ESPresense.Companion.Tests.Optimizers.DeviceResidualOptimizerTests;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The runner's device term: a proposal that fixes how a node hears devices is scored on held-out device
/// windows, not only on node-to-node links that cannot see it; learned device rssi@1m is written only on its
/// own guards; and with device_weight 0 the device windows change nothing.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceResidualRunnerTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private NodeSettingsStore _nss = null!;
    private DeviceSettingsStore _dss = null!;
    private OptimizationRunner _runner = null!;
    private FakeLogger<OptimizationRunner> _log = null!;
    private string _configDir = null!;

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
        _nss = new NodeSettingsStore(_mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        _dss = new DeviceSettingsStore(_mqtt.Object, _state);
        _log = new FakeLogger<OptimizationRunner>();
        _runner = new OptimizationRunner(_state, _nss, _log, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance, _dss);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private static readonly Dictionary<string, OptNode> Nodes = DeviceGridSolverTests.House
        .ToDictionary(n => n.Id, n => new OptNode { Id = n.Id, Name = n.Id, Location = new Point3D(n.X, n.Y, n.Z) });

    private static double Gauss(Random rnd) => Math.Sqrt(-2 * Math.Log(1.0 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());

    /// <summary>Node-to-node measures: every receiver hears every other node with <paramref name="rxBias"/> and 2 dB of noise.</summary>
    private static List<Measure> NodeToNode(Func<string, double> rxBias, int seed)
    {
        var rnd = new Random(seed);
        var measures = new List<Measure>();
        foreach (var rx in Nodes.Values)
            foreach (var tx in Nodes.Values)
            {
                var d = rx.Location.DistanceTo(tx.Location);
                if (ReferenceEquals(rx, tx) || d <= 1.2) continue;
                measures.Add(new Measure
                {
                    Rx = rx, Tx = tx, RssiRxAdj = 0, RefRssi = -59, Distance = d,
                    Rssi = -59 + rxBias(rx.Id) - 10 * DeviceResidualModel.DefaultAbsorption * Math.Log10(d) + 2 * Gauss(rnd),
                });
            }
        return measures;
    }

    /// <summary>
    /// A training snapshot holding the older 60% of the device windows and three held-out snapshots holding a
    /// third of the rest each; every snapshot also has its own node-to-node measures.
    /// </summary>
    private static (OptimizationSnapshot Training, List<OptimizationSnapshot> Eval) Scenario(
        Func<string, double> nodeToNodeBias, Func<string, double> deviceBias, double walker2Offset = 0)
    {
        var walkers = Enumerable.Range(0, 5).Select(i => Walker($"walker{i}", 400 + i, -59 + (i == 2 ? walker2Offset : 0))).ToList();
        var windows = Windows(walkers, 240, deviceBias).OrderBy(w => w.End).ToList();
        var geometry = Geometry();
        var cut = windows.Count * 6 / 10;
        var training = new OptimizationSnapshot { Measures = NodeToNode(nodeToNodeBias, 1), DeviceWindows = windows.Take(cut).ToList(), Geometry = geometry };
        var rest = windows.Skip(cut).ToList();
        var eval = Enumerable.Range(0, 3).Select(k => new OptimizationSnapshot
        {
            Measures = NodeToNode(nodeToNodeBias, 10 + k),
            DeviceWindows = rest.Where((_, i) => i * 3 / rest.Count == k).ToList(),
            Geometry = geometry,
        }).ToList();
        return (training, eval);
    }

    private static ConfigOptimization Optimization(double deviceWeight) => new()
    {
        Enabled = true,
        Optimizer = "device_residual",
        MinSamples = 5,
        DeviceWeight = deviceWeight,
    };

    private DeviceResidualOptimizer NewOptimizer() => new(_state, NullLogger<DeviceResidualOptimizer>.Instance);

    private void DumpLog()
    {
        foreach (var e in _log.Collector.GetSnapshot()) TestContext.Out.WriteLine($"{e.Level}: {e.Message}");
    }

    /// <summary>
    /// The case the robot survey found: a node a few dB hot node-to-node and twice that to devices. Node-to-node
    /// scoring alone prefers its node-to-node value; with the device term the device-facing value wins.
    /// </summary>
    [Test]
    public async Task DeviceFacingProposal_WinsWithTheDeviceTerm()
    {
        var (training, eval) = Scenario(id => id == "n03" ? 5 : 0, id => id == "n03" ? 10 : 0);

        await _runner.RunCycleAsync(training, eval, Optimization(1.0), new List<IOptimizer> { NewOptimizer() });
        DumpLog();

        var rx = _nss.Get("n03").Calibration.RxAdjRssi;
        Assert.That(rx, Is.Not.Null, "the device-residual proposal should have been applied");
        Assert.That(rx!.Value, Is.GreaterThanOrEqualTo(4), "n03 moves toward its device-facing offset (capped at 5 dB a cycle)");

        // The last-cycle record shows the device term behind the decision.
        var report = _runner.LastCycle!.Report;
        Assert.That(report.Settings.DeviceWeight, Is.EqualTo(1.0));
        Assert.That(report.DeviceWindows!.Eval, Is.EqualTo(eval.Sum(e => e.DeviceWindows.Count)));
        var baseline = report.Baseline.Device!;
        var proposal = report.Proposals.Single();
        var device = proposal.Score.Device!;
        Assert.That(device.DeviceRmse, Is.LessThan(baseline.DeviceRmse));
        Assert.That(device.Term, Is.EqualTo(1.0 * (1 - device.DeviceRmse / (1 + device.DeviceRmse))).Within(1e-12));
        Assert.That(proposal.Decision, Is.EqualTo(OptimizerGateDecision.Selected));
        Assert.That(proposal.Applied, Is.True);
        Assert.That(proposal.DeviceResidual, Is.Not.Null, "what the optimizer learned on its training windows");
        Assert.That(_state.OptimizerState.DeviceResidual, Is.SameAs(proposal.DeviceResidual), "the live cycle publishes it");
    }

    /// <summary>
    /// A node that hears only devices hot (node-to-node it is fine): node-to-node scoring prefers leaving it, so
    /// the device-facing value only wins once the device term is weighted enough. On this house the spec's
    /// default weight (1) stops at the node-to-node optimum; 3 reaches the device-facing value.
    /// </summary>
    [TestCase(1.0, 0.0, 0.0)]
    [TestCase(3.0, 0.0, 8.5)]
    [TestCase(1.0, 5.0, 4.5)]
    [TestCase(3.0, 5.0, 8.5)]
    public async Task CompetingOptimizers_DeviceWeightDecidesHowFarTheDeviceFacingValueGets(double weight, double nodeToNode, double atLeast)
    {
        var (training, eval) = Scenario(id => id == "n03" ? nodeToNode : 0, id => id == "n03" ? 10 : 0);
        var optimizers = new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state, NullLogger<PerNodeAbsorptionRxTx>.Instance), NewOptimizer() };

        var trail = new List<double>();
        for (int c = 0; c < 3; c++)
        {
            await _runner.RunCycleAsync(training, eval, Optimization(weight), optimizers);
            trail.Add((_nss.Get("n03").Calibration.RxAdjRssi ?? 0)
                      - DeviceGridSolverTests.House.Where(n => n.Id != "n03").Average(n => _nss.Get(n.Id).Calibration.RxAdjRssi ?? 0));
        }

        TestContext.Out.WriteLine($"device_weight {weight}, n03 {nodeToNode} dB hot node-to-node and 10 dB to devices: n03 relative rxAdj per cycle {string.Join(" -> ", trail.Select(t => t.ToString("0.0")))}");
        Assert.That(trail[^1], Is.GreaterThanOrEqualTo(atLeast));
        Assert.That(trail[^1], Is.LessThanOrEqualTo(11.5));
    }

    [Test]
    public async Task DeviceWeightZero_NodeToNodeAloneRejectsIt()
    {
        var (training, eval) = Scenario(_ => 0, id => id == "n03" ? 10 : 0);

        await _runner.RunCycleAsync(training, eval, Optimization(0), new List<IOptimizer> { NewOptimizer() });

        Assert.That(_nss.Get("n03").Calibration.RxAdjRssi, Is.Null, "node-to-node scoring cannot see a device-facing bias");
        Assert.That(_log.Collector.GetSnapshot().Select(e => e.Message), Has.None.Contains("device residual:"),
            "with device_weight 0 the device term is not computed at all");
        Assert.That(_runner.LastCycle!.Report.Baseline.Device, Is.Null);
        Assert.That(_runner.LastCycle!.Report.Proposals.Single().Score.Device, Is.Null);
    }

    /// <summary>With device_weight 0, a cycle over snapshots that carry device windows ends exactly as one without them.</summary>
    [Test]
    public async Task DeviceWeightZero_DeviceWindowsChangeNothing()
    {
        var (training, eval) = Scenario(id => id == "n03" ? 5 : 0, _ => 0);
        var bare = (Training: new OptimizationSnapshot { Measures = training.Measures },
                    Eval: eval.Select(e => new OptimizationSnapshot { Measures = e.Measures }).ToList());
        var optimization = Optimization(0);
        optimization.Optimizer = "per_node_absorption";

        await _runner.RunCycleAsync(bare.Training, bare.Eval, optimization, new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state, NullLogger<PerNodeAbsorptionRxTx>.Instance) });
        var without = (_state.OptimizerState.BestR, _state.OptimizerState.BestRMSE, _state.OptimizerState.LastOutcome, Rx: _nss.Get("n03").Calibration.RxAdjRssi);
        var calls = _mqtt.Invocations.Select(i => string.Join("|", i.Arguments)).ToList();
        var messages = _log.Collector.GetSnapshot().Select(e => e.Message).ToList();

        await TearDown();
        await Setup();
        await _runner.RunCycleAsync(training, eval, optimization, new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state, NullLogger<PerNodeAbsorptionRxTx>.Instance) });

        Assert.That((_state.OptimizerState.BestR, _state.OptimizerState.BestRMSE, _state.OptimizerState.LastOutcome, _nss.Get("n03").Calibration.RxAdjRssi), Is.EqualTo(without));
        Assert.That(_mqtt.Invocations.Select(i => string.Join("|", i.Arguments)), Is.EqualTo(calls));
        Assert.That(_log.Collector.GetSnapshot().Select(e => e.Message), Is.EqualTo(messages));
    }

    /// <summary>
    /// A device 8 dB quieter than its rssi@1m: its learned value is written to its device settings once it has
    /// held still for two cycles, under the original id; the other devices, already right, are not written.
    /// </summary>
    [Test]
    public async Task LearnedDeviceReference_IsWrittenOnceStable()
    {
        var (training, eval) = Scenario(_ => 0, _ => 0, walker2Offset: -8);
        var optimizers = new List<IOptimizer> { NewOptimizer() };
        var optimization = Optimization(1.0);
        var topic = MqttTopics.DeviceSettings("walker2");

        var writtenAt = new List<int>();
        for (int c = 0; c < 4; c++)
        {
            await _runner.RunCycleAsync(training, eval, optimization, optimizers);
            if (_mqtt.Invocations.Any(i => i.Arguments.Count > 0 && Equals(i.Arguments[0], topic))) writtenAt.Add(c);
        }

        var writes = _mqtt.Invocations.Where(i => Equals(i.Arguments[0], topic)).Select(i => (string)i.Arguments[1]!).ToList();
        TestContext.Out.WriteLine($"walker2 written in cycles {string.Join(", ", writtenAt)}: {writes.FirstOrDefault()}");
        Assert.That(writtenAt, Is.Not.Empty, "written once it has settled");
        Assert.That(writtenAt[0], Is.GreaterThanOrEqualTo(2), "never before two cycles in which it held within 1 dB");
        Assert.That(writes[0], Does.Contain("\"rssi@1m\":-67").Or.Contain("\"rssi@1m\":-66").Or.Contain("\"rssi@1m\":-68"));
        Assert.That(_mqtt.Invocations.Count(i => i.Arguments[0] is string t && t.StartsWith("espresense/settings/walker") && t != topic), Is.Zero,
            "devices already ranged right are not written");
        Assert.That(_state.OptimizerState.DeviceResidual!.DevicesApplied, Is.EqualTo(1));

        // The last-cycle record lists each learned rssi@1m with the guards' decision.
        var devices = _runner.LastCycle!.Report.Devices!;
        var walker2 = devices.Single(d => d.Device == "walker2");
        Assert.That(walker2.Decision, Is.EqualTo(OptimizerDeviceReferenceDecision.Apply));
        Assert.That(walker2.Applied, Is.True);
        Assert.That(walker2.RefRssi, Is.InRange(-68, -66));
        Assert.That(devices.Where(d => d.Device != "walker2").Select(d => d.Decision), Is.All.EqualTo(OptimizerDeviceReferenceDecision.Unchanged));
        Assert.That(_runner.LastCycle!.Report.Outcome, Does.Contain("Wrote learned rssi@1m to 1 device."));
    }

    [Test]
    public async Task LearnedDeviceReference_NotWrittenWhenApplyDeviceRefIsOff()
    {
        var (training, eval) = Scenario(_ => 0, _ => 0, walker2Offset: -8);
        var optimizers = new List<IOptimizer> { NewOptimizer() };
        var optimization = Optimization(1.0);
        optimization.DeviceResidual.ApplyDeviceRef = false;

        for (int c = 0; c < 4; c++)
            await _runner.RunCycleAsync(training, eval, optimization, optimizers);

        Assert.That(_mqtt.Invocations.Count(i => i.Arguments[0] is string t && t.StartsWith("espresense/settings/")), Is.Zero);
    }

    /// <summary>Device settings with an alias live under the original id; writing to the alias is refused, so the runner writes there.</summary>
    [Test]
    public async Task LearnedDeviceReference_IsWrittenUnderTheOriginalId()
    {
        var store = new Mock<DeviceSettingsStore>(_mqtt.Object, _state) { CallBase = true };
        store.Setup(s => s.Get("phone")).Returns(new DeviceSettings { Id = "phone", OriginalId = "raw-phone", Name = "Phone", RefRssi = -59 });
        var runner = new OptimizationRunner(_state, _nss, _log, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance, store.Object);
        var proposals = new Dictionary<string, ProposedDeviceValues>
        {
            ["phone"] = new() { RefRssi = -66.2, CurrentRefRssi = -59, Windows = 500, Mobility = 0.8, StableCycles = 2 },
        };

        var applied = await runner.ApplyDeviceReferencesAsync(proposals, Optimization(1.0));

        Assert.That(applied, Is.EqualTo(1));
        store.Verify(s => s.Set("raw-phone", It.Is<DeviceSettings>(d => d.RefRssi == -66 && d.Id == "phone" && d.Name == "Phone")), Times.Once);
    }

    [TestCase(49, 0.8, 2, TestName = "TooFewWindows")]
    [TestCase(500, 0.2, 2, TestName = "SitsStill")]
    [TestCase(500, 0.8, 1, TestName = "NotYetStable")]
    public async Task LearnedDeviceReference_GuardsHold(int windows, double mobility, int stable)
    {
        var proposals = new Dictionary<string, ProposedDeviceValues>
        {
            ["phone"] = new() { RefRssi = -66, CurrentRefRssi = -59, Windows = windows, Mobility = mobility, StableCycles = stable },
        };

        Assert.That(await _runner.ApplyDeviceReferencesAsync(proposals, Optimization(1.0)), Is.Zero);
        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>Held-out history for a dry run: snapshots 30 s apart, each with its own node-to-node measures and device windows.</summary>
    private static List<OptimizationSnapshot> History(Func<string, double> deviceBias, double walker2Offset = 0)
    {
        var walkers = Enumerable.Range(0, 5).Select(i => Walker($"walker{i}", 400 + i, -59 + (i == 2 ? walker2Offset : 0))).ToList();
        var windows = Windows(walkers, 240, deviceBias).OrderBy(w => w.End).ToList();
        const int count = 8;
        var start = DateTime.UtcNow.AddMinutes(-20);
        return Enumerable.Range(0, count).Select(k => new OptimizationSnapshot
        {
            Timestamp = start.AddSeconds(30 * k),
            Measures = NodeToNode(_ => 0, 20 + k),
            DeviceWindows = windows.Where((_, i) => i * count / windows.Count == k).ToList(),
            Geometry = Geometry(),
        }).ToList();
    }

    /// <summary>
    /// A dry run in device_residual mode reports the device term, the learned rssi@1m and their decisions, and
    /// applies nothing: no node or device settings written, no OptimizerState touched, the live optimizer not advanced.
    /// </summary>
    [Test]
    public async Task DryRun_ReportsTheDeviceTermAndReferences_AndChangesNothing()
    {
        _configLoader.Config!.Optimization.Optimizer = "device_residual";
        _configLoader.Config!.Optimization.Enabled = true;
        var runner = new OptimizationRunner(_state, _nss, _log, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance, _dss);
        runner.Snapshots.AddRange(History(id => id == "n03" ? 10 : 0, walker2Offset: -8));
        Assert.That(runner.CurrentOptimizers.OfType<DeviceResidualOptimizer>(), Is.Not.Empty);

        var report = (await runner.DryRunAsync(null)).Report;

        Assert.That(report.DryRun, Is.True);
        Assert.That(report.Proposals.Select(p => p.Optimizer), Does.Contain("Device Residual"));
        Assert.That(report.Baseline.Device, Is.Not.Null);
        Assert.That(report.Proposals.Single(p => p.Optimizer == "Device Residual").Score.Device, Is.Not.Null);
        Assert.That(report.Devices, Is.Not.Empty);
        Assert.That(report.Devices!.All(d => !d.Applied), Is.True);
        Assert.That(report.Devices!.Single(d => d.Device == "walker2").Decision, Is.EqualTo(OptimizerDeviceReferenceDecision.NotYetStable),
            "a fresh history has not held still for two cycles yet");
        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        Assert.That(_state.OptimizerState.DeviceResidual, Is.Null);
        Assert.That(runner.LastCycle, Is.Null);
    }
}
