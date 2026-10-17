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
}
