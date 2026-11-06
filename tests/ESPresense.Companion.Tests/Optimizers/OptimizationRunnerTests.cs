using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OptimizationRunnerTests
{
    private const string AbsorptionTopic = "espresense/rooms/rx/absorption/set";

    private Mock<IMqttCoordinator> _mqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private NodeSettingsStore _nss = null!;
    private OptimizationRunner _runner = null!;
    private FakeLogger<OptimizationRunner> _log = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));

        // These tests build floors/nodes from Config; the constructor no longer loads, so load explicitly.
        await _configLoader.LoadAsync();
        _nss = new NodeSettingsStore(_mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        _log = new FakeLogger<OptimizationRunner>();
        _runner = new OptimizationRunner(_state, _nss, _log, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();

        if (Directory.Exists(_configDir))
        {
            try
            {
                Directory.Delete(_configDir, recursive: true);
            }
            catch (IOException)
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Receiver at the origin and <paramref name="measurementCount"/> transmitters at 2, 3, ... metres whose RSSI is
    /// generated from an absorption of exactly 3.0, so a proposal of 3.0 fits perfectly (RMSE 0) while the 2.7
    /// default used by the baseline does not. Every such snapshot therefore "beats" the baseline on score alone.
    /// </summary>
    private static OptimizationSnapshot SnapshotWith(int measurementCount)
    {
        var rx = new OptNode { Id = "rx", Name = "rx", Location = new Point3D(0, 0, 0) };
        var snapshot = new OptimizationSnapshot();
        for (int i = 0; i < measurementCount; i++)
        {
            double distance = i + 2;
            var tx = new OptNode { Id = $"tx{i}", Name = $"tx{i}", Location = new Point3D(distance, 0, 0) };
            snapshot.Measures.Add(new Measure
            {
                Rx = rx,
                Tx = tx,
                Rssi = -59 - 10 * 3.0 * Math.Log10(distance),
                RefRssi = -59,
                Distance = distance
            });
        }
        return snapshot;
    }

    private sealed class FixedAbsorptionOptimizer : IOptimizer
    {
        public string Name => "Fixed";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            results.Nodes["rx"] = new ProposedValues { Absorption = 3.0 };
            return results;
        }
    }

    /// <summary>
    /// Proposes the exact absorption plus a txRef/rxAdj shift that cancels node-to-node: every
    /// transmitter 10 dB louder, the receiver 10 dB deafer. Fits the snapshot perfectly, and would
    /// make every device (ranged with its own rssi@1m) read 10 dB too far.
    /// </summary>
    private sealed class GaugeShiftedOptimizer : IOptimizer
    {
        public string Name => "GaugeShifted";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            results.Nodes["rx"] = new ProposedValues { Absorption = 3.0, RxAdjRssi = -10 };
            foreach (var tx in os.Measures.Select(m => m.Tx.Id).Distinct())
                results.Nodes[tx] = new ProposedValues { TxRefRssi = -49 };
            return results;
        }
    }

    private Task RunCycle(OptimizationSnapshot snapshot, ConfigOptimization optimization, IOptimizer? optimizer = null)
    {
        return _runner.RunCycleAsync(
            snapshot,
            new List<OptimizationSnapshot> { snapshot },
            optimization,
            new List<IOptimizer> { optimizer ?? new FixedAbsorptionOptimizer() });
    }

    [Test]
    public async Task RunCycle_RejectedProposal_LogsEachNodesValuesAtInformation()
    {
        var snapshot = SnapshotWith(5);
        foreach (var m in snapshot.Measures) m.RssiRxAdj = 0;
        var incumbent = _nss.Get("rx");
        incumbent.Calibration.RxAdjRssi = 2;
        await _nss.Set("rx", incumbent);

        await RunCycle(snapshot, new ConfigOptimization { Enabled = true, MinSamples = 5 }, new GaugeShiftedOptimizer());

        var info = _log.Collector.GetSnapshot().Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        Assert.That(info, Has.Some.Contains("rejected: better node-to-node"), "sanity: the device-frame gate should have rejected it");
        Assert.That(info, Has.One.Matches<string>(m => m.StartsWith("Rejected GaugeShifted rx ") && m.Contains("absorption=3.00 (was unset, scored as 2.70)") && m.Contains("rx_adj=-10 (was 2)")),
            "the rejected proposal's receiver values should be logged beside the incumbent's");
        Assert.That(info.Count(m => m.StartsWith("Rejected GaugeShifted tx") && m.Contains("tx_ref=-49 (was unset, scored as -59)")), Is.EqualTo(5),
            "one line per transmitter in the proposal");
    }

    /// <summary>Proposes like <see cref="GaugeShiftedOptimizer"/>, but first runs <paramref name="duringFit"/>, standing in for an MQTT echo that lands mid-cycle.</summary>
    private sealed class DuringFitOptimizer(Action duringFit) : IOptimizer
    {
        public string Name => "GaugeShifted";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            duringFit();
            return new GaugeShiftedOptimizer().Optimize(os, existingSettings);
        }
    }

    [Test]
    public async Task RunCycle_RejectedProposal_LogsTheIncumbentItWasScoredAgainst()
    {
        var snapshot = SnapshotWith(5);
        foreach (var m in snapshot.Measures) m.RssiRxAdj = 0;
        var incumbent = _nss.Get("rx");
        incumbent.Calibration.RxAdjRssi = 2;
        await _nss.Set("rx", incumbent);

        var echo = new DuringFitOptimizer(() =>
        {
            var changed = _nss.Get("rx");
            changed.Calibration.RxAdjRssi = 7;
            _nss.Set("rx", changed).GetAwaiter().GetResult();
        });
        await RunCycle(snapshot, new ConfigOptimization { Enabled = true, MinSamples = 5 }, echo);

        Assert.That(_nss.Get("rx").Calibration.RxAdjRssi, Is.EqualTo(7), "sanity: the store changed while the cycle ran");
        var info = _log.Collector.GetSnapshot().Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();
        Assert.That(info, Has.One.Matches<string>(m => m.StartsWith("Rejected GaugeShifted rx ") && m.Contains("rx_adj=-10 (was 2)")),
            "the log should show the frozen incumbent the proposal was scored against, not the store after the cycle");
    }

    [Test]
    public void DescribeProposal_LeavesOutValuesNotProposed()
    {
        var current = new CalibrationSettings { Absorption = 2.95, RxAdjRssi = -4, TxRefRssi = -59 };

        Assert.That(OptimizationRunner.DescribeProposal(new OptimizerNodeProposal(3.1, 3, -61, 4.2), current),
            Is.EqualTo("absorption=3.10 (was 2.95), rx_adj=3 (was -4), tx_ref=-61 (was -59), error=4.20"));
        Assert.That(OptimizationRunner.DescribeProposal(new OptimizerNodeProposal(null, 1, null, null), current), Is.EqualTo("rx_adj=1 (was -4)"));
        Assert.That(OptimizationRunner.DescribeProposal(new OptimizerNodeProposal(null, null, null, null), current), Is.EqualTo("no values"));
        Assert.That(OptimizationRunner.DescribeProposal(new OptimizerNodeProposal(3.1, -0.4, -61, null), new CalibrationSettings()),
            Is.EqualTo("absorption=3.10 (was unset, scored as 2.70), rx_adj=0 (was unset, scored as 0), tx_ref=-61 (was unset, scored as -59)"),
            "an unset incumbent is shown with the default it is scored with, and -0 as 0");
    }

    [Test]
    public async Task RunCycle_TxRefRxAdjShiftThatCancelsNodeToNode_IsRejected()
    {
        var snapshot = SnapshotWith(5);
        foreach (var m in snapshot.Measures) m.RssiRxAdj = 0; // measurements carry their rxAdj, so a proposal can re-base them

        await RunCycle(snapshot, new ConfigOptimization { Enabled = true, MinSamples = 5 }, new GaugeShiftedOptimizer());

        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never,
            "a proposal that only wins by shifting txRef against rxAdj must not be applied");
        Assert.That(_nss.Get("rx").Calibration.RxAdjRssi, Is.Null);
        Assert.That(_nss.Get("tx0").Calibration.TxRefRssi, Is.Null);
    }

    [Test]
    public async Task RunCycle_SingleMeasurement_IsRejectedByDefaultMinSamples()
    {
        await RunCycle(SnapshotWith(1), new ConfigOptimization { Enabled = true });

        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task RunCycle_SingleMeasurement_WouldBeAppliedWithoutTheGuard()
    {
        // Proves the guard is load-bearing: a one-point fit beats the baseline on score, so only min_samples stops it.
        await RunCycle(SnapshotWith(1), new ConfigOptimization { Enabled = true, MinSamples = 1 });

        _mqtt.Verify(x => x.EnqueueAsync(AbsorptionTopic, It.IsAny<string?>(), false), Times.Once);
    }

    [Test]
    public async Task RunCycle_FewerThanMinSamples_IsRejected()
    {
        await RunCycle(SnapshotWith(4), new ConfigOptimization { Enabled = true, MinSamples = 5 });

        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task RunCycle_GainBelowMinimumImprovement_IsNotApplied()
    {
        await RunCycle(SnapshotWith(5), new ConfigOptimization { Enabled = true, MinSamples = 5, MinimumImprovement = 2 });

        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task RunCycle_AtLeastMinSamples_IsEvaluatedAndApplied()
    {
        await RunCycle(SnapshotWith(5), new ConfigOptimization { Enabled = true, MinSamples = 5 });

        _mqtt.Verify(x => x.EnqueueAsync(AbsorptionTopic, It.IsAny<string?>(), false), Times.Once);
        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
    }

    private static ConfigOptimization Limits(params (string Key, double Value)[] limits) =>
        new() { Limits = limits.ToDictionary(l => l.Key, l => l.Value) };

    [Test]
    public void LimitsReached_ValueThatRoundsOntoTheLimit_IsReported()
    {
        // The runner stores rxAdj 29.6 as 30, so against a limit of 30 it is applied at the limit.
        var reached = OptimizationRunner.LimitsReached(null, (int)Math.Round(29.6), null, Limits(("rx_adj_rssi_max", 30)));

        Assert.That(reached, Is.EqualTo(new[] { "rx_adj_rssi=30 (rx_adj_rssi_max)" }));
    }

    [Test]
    public void LimitsReached_AbsorptionPublishedOntoTheLimit_IsReported()
    {
        // Absorption goes out to two decimals: 3.996 is published as 4.00.
        Assert.That(OptimizationRunner.LimitsReached(3.996, null, null, Limits(("absorption_max", 4))),
            Is.EqualTo(new[] { "absorption=4.00 (absorption_max)" }));
        Assert.That(OptimizationRunner.LimitsReached(3.99, null, null, Limits(("absorption_max", 4))), Is.Empty);
    }

    [Test]
    public void LimitsReached_EachLimitAndEdge_IsNamed()
    {
        var reached = OptimizationRunner.LimitsReached(2.0, 30, -70, new ConfigOptimization());

        Assert.That(reached, Is.EqualTo(new[]
        {
            "absorption=2.00 (absorption_min)",
            "rx_adj_rssi=30 (rx_adj_rssi_max)",
            "tx_ref_rssi=-70 (tx_ref_rssi_min)"
        }));
    }

    [Test]
    public void LimitsReached_InsideLimits_OrNotApplied_IsNotReported()
    {
        Assert.That(OptimizationRunner.LimitsReached(3.0, 29, -69, new ConfigOptimization()), Is.Empty);
        Assert.That(OptimizationRunner.LimitsReached(null, null, null, new ConfigOptimization()), Is.Empty);
    }

    [Test]
    public void LimitsReached_LimitPinnedOnPurpose_IsNotReported()
    {
        // min == max is how a value is held fixed; it sits on the limit by design every cycle.
        var reached = OptimizationRunner.LimitsReached(3.0, null, null, Limits(("absorption_min", 3), ("absorption_max", 3)));

        Assert.That(reached, Is.Empty);
    }


    private sealed class AbsorptionOptimizer(double absorption) : IOptimizer
    {
        public string Name => "Absorption";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            results.Nodes["rx"] = new ProposedValues { Absorption = absorption };
            return results;
        }
    }

    private sealed class CountingNodeSettingsStore(IMqttCoordinator mqtt) : NodeSettingsStore(mqtt, Mock.Of<ILogger<NodeSettingsStore>>())
    {
        public Dictionary<string, int> Reads { get; } = new();

        public override NodeSettings Get(string id)
        {
            Reads[id] = Reads.GetValueOrDefault(id) + 1;
            return base.Get(id);
        }
    }

    [Test]
    public async Task RunCycle_ScoresAProposalAtTheValueThatWillBeApplied()
    {
        await RunCycle(SnapshotWith(5), new ConfigOptimization { Enabled = true, MinSamples = 5 }, new AbsorptionOptimizer(2.9996));

        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
    }

    [Test]
    public async Task RunCycle_ReadsEachNodesSettingsOnceBeforeScoring()
    {
        var store = new CountingNodeSettingsStore(_mqtt.Object);
        var runner = new OptimizationRunner(_state, store, NullLogger<OptimizationRunner>.Instance, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance);
        var snapshot = SnapshotWith(5);

        await runner.RunCycleAsync(
            snapshot,
            new List<OptimizationSnapshot> { snapshot },
            new ConfigOptimization { Enabled = true, MinSamples = 5 },
            new List<IOptimizer> { new FixedAbsorptionOptimizer() });

        // Scoring dozens of measurements must not re-read the live store each time: one frozen read at cycle
        // start, then only the reads made while applying the winning proposal (rx is read to apply and again by Set).
        Assert.That(store.Reads["rx"], Is.EqualTo(3));
        Assert.That(store.Reads["tx0"], Is.EqualTo(1));
    }

    private static List<OptimizationSnapshot> TimedSnapshots(int count, TimeSpan spacing, int measurementCount = 5)
    {
        var start = DateTime.UtcNow - spacing * (count - 1);
        return Enumerable.Range(0, count)
            .Select(i => new OptimizationSnapshot { Timestamp = start + spacing * i, Measures = SnapshotWith(measurementCount).Measures })
            .ToList();
    }

    [Test]
    public void HasSufficientHistory_RequiresHalfTheTrainingWindow()
    {
        var optimization = new ConfigOptimization { TrainingWindowMins = 30 };

        Assert.That(OptimizationRunner.HasSufficientHistory(TimedSnapshots(4, TimeSpan.FromSeconds(30)), optimization), Is.False, "90 seconds of history");
        Assert.That(OptimizationRunner.HasSufficientHistory(TimedSnapshots(31, TimeSpan.FromSeconds(30)), optimization), Is.True, "15 minutes of history");
        Assert.That(OptimizationRunner.HasSufficientHistory(TimedSnapshots(2, TimeSpan.FromMinutes(20)), optimization), Is.False, "too few snapshots to split");
    }

    [Test]
    public async Task ScheduledCycle_WithTooLittleHistory_AppliesNothing()
    {
        _runner.Snapshots.AddRange(TimedSnapshots(4, TimeSpan.FromSeconds(30)));

        var ran = await _runner.RunScheduledCycleAsync(new ConfigOptimization { Enabled = true, TrainingWindowMins = 30 }, new List<IOptimizer> { new FixedAbsorptionOptimizer() });

        Assert.That(ran, Is.False);
        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task ScheduledCycle_WithEnoughHistory_FitsOlderDataAndValidatesOnNewer()
    {
        _runner.Snapshots.AddRange(TimedSnapshots(31, TimeSpan.FromSeconds(30)));

        var ran = await _runner.RunScheduledCycleAsync(new ConfigOptimization { Enabled = true, TrainingWindowMins = 30 }, new List<IOptimizer> { new FixedAbsorptionOptimizer() });

        Assert.That(ran, Is.True);
        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
        // 31 snapshots of 5 measurements, 20% (7 snapshots) held out
        Assert.That(_state.OptimizerState.TrainingSamples, Is.EqualTo(24 * 5));
        Assert.That(_state.OptimizerState.ValidationSamples, Is.EqualTo(7 * 5));
    }

    [TestCase(3.0, 3.0, true)]
    [TestCase(2.996, 3.0, true)]
    [TestCase(3.0, 4.0, false)]
    public async Task RunCycle_AppliedValueAtLimit_LogsWarning(double proposed, double absorptionMax, bool expectWarning)
    {
        var optimization = new ConfigOptimization { Enabled = true, MinSamples = 5, Limits = { ["absorption_max"] = absorptionMax } };
        await RunCycle(SnapshotWith(5), optimization, new AbsorptionOptimizer(proposed));

        _mqtt.Verify(x => x.EnqueueAsync(AbsorptionTopic, It.IsAny<string?>(), false), Times.Once, "the proposal should have been applied");
        var warnings = _log.Collector.GetSnapshot().Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        if (expectWarning)
            Assert.That(warnings, Has.One.Contains("absorption=3.00 (absorption_max)"));
        else
            Assert.That(warnings, Is.Empty);
    }
}
