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

    private Task RunCycle(OptimizationSnapshot snapshot, ConfigOptimization optimization)
    {
        return _runner.RunCycleAsync(
            snapshot,
            new List<OptimizationSnapshot> { snapshot },
            optimization,
            new List<IOptimizer> { new FixedAbsorptionOptimizer() });
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
