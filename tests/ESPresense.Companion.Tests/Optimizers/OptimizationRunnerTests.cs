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

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
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

    [TestCase(3.0, 3.0, true)]
    [TestCase(2.996, 3.0, true)]
    [TestCase(3.0, 4.0, false)]
    public async Task RunCycle_AppliedValueAtLimit_LogsWarning(double proposed, double absorptionMax, bool expectWarning)
    {
        var sink = new CapturingSink();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var optimization = new ConfigOptimization { Enabled = true, MinSamples = 5, Limits = { ["absorption_max"] = absorptionMax } };
            await RunCycle(SnapshotWith(5), optimization, new AbsorptionOptimizer(proposed));
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        _mqtt.Verify(x => x.EnqueueAsync(AbsorptionTopic, It.IsAny<string?>(), false), Times.Once, "the proposal should have been applied");
        var warnings = sink.Events.Where(e => e.Level == Serilog.Events.LogEventLevel.Warning).Select(e => e.RenderMessage()).ToList();
        if (expectWarning)
            Assert.That(warnings, Has.One.Contains("absorption=3.00 (absorption_max)"));
        else
            Assert.That(warnings, Is.Empty);
    }
}
