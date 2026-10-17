using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));
        _nss = new NodeSettingsStore(_mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        _runner = new OptimizationRunner(_state, _nss, NullLogger<OptimizationRunner>.Instance, _configLoader, Mock.Of<ILeaseService>());
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
    public async Task RunCycle_AtLeastMinSamples_IsEvaluatedAndApplied()
    {
        await RunCycle(SnapshotWith(5), new ConfigOptimization { Enabled = true, MinSamples = 5 });

        _mqtt.Verify(x => x.EnqueueAsync(AbsorptionTopic, It.IsAny<string?>(), false), Times.Once);
        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
    }

    private sealed class UnroundedAbsorptionOptimizer : IOptimizer
    {
        public string Name => "Unrounded";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            results.Nodes["rx"] = new ProposedValues { Absorption = 2.9996 };
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
        await RunCycle(SnapshotWith(5), new ConfigOptimization { Enabled = true, MinSamples = 5 }, new UnroundedAbsorptionOptimizer());

        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
    }

    [Test]
    public async Task RunCycle_ReadsEachNodesSettingsOnceBeforeScoring()
    {
        var store = new CountingNodeSettingsStore(_mqtt.Object);
        var runner = new OptimizationRunner(_state, store, NullLogger<OptimizationRunner>.Instance, _configLoader, Mock.Of<ILeaseService>());
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
        _state.OptimizationSnaphots.AddRange(TimedSnapshots(4, TimeSpan.FromSeconds(30)));

        var ran = await _runner.RunScheduledCycleAsync(new ConfigOptimization { Enabled = true, TrainingWindowMins = 30 }, new List<IOptimizer> { new FixedAbsorptionOptimizer() });

        Assert.That(ran, Is.False);
        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task ScheduledCycle_WithEnoughHistory_FitsOlderDataAndValidatesOnNewer()
    {
        _state.OptimizationSnaphots.AddRange(TimedSnapshots(31, TimeSpan.FromSeconds(30)));

        var ran = await _runner.RunScheduledCycleAsync(new ConfigOptimization { Enabled = true, TrainingWindowMins = 30 }, new List<IOptimizer> { new FixedAbsorptionOptimizer() });

        Assert.That(ran, Is.True);
        Assert.That(_nss.Get("rx").Calibration.Absorption, Is.EqualTo(3.0));
        // 31 snapshots of 5 measurements, 20% (7 snapshots) held out
        Assert.That(_state.OptimizerState.TrainingSamples, Is.EqualTo(24 * 5));
        Assert.That(_state.OptimizerState.ValidationSamples, Is.EqualTo(7 * 5));
    }
}
