using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// Regression tests for the "all nodes pinned to the same absorption" degeneracy.
///
/// RxAdjRssiOptimizer fits only RxAdjRssi; it holds the path-loss exponent fixed at the
/// midpoint of the configured bounds as a modeling constant. It used to emit that constant
/// as a proposed Absorption, and because it runs first in the legacy pipeline and the runner
/// applies any non-null Absorption, every node's real per-node absorption got overwritten with
/// the same value. These tests pin the contract that it never proposes Absorption.
/// </summary>
public class RxAdjRssiOptimizerTests
{
    private State _state = null!;
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private NodeTelemetryStore _nodeTelemetryStore = null!;
    private Mock<IMqttCoordinator> _mockMqttCoordinator = null!;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();

        if (Directory.Exists(_configDir))
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Directory.Delete(_configDir, recursive: true);
                    break;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(100);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
        }
    }

    [Test]
    public void Optimize_DoesNotPropose_Absorption()
    {
        var snapshot = BuildSnapshotWithReceiver("rx1", txCount: 4);

        var optimizer = new RxAdjRssiOptimizer(_state);
        var results = optimizer.Optimize(snapshot, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Not.Empty, "Optimizer should produce a result for a receiver hearing >=3 transmitters");
        foreach (var (id, proposed) in results.Nodes)
        {
            Assert.That(proposed.Absorption, Is.Null,
                $"RxAdjRssiOptimizer must not propose Absorption for {id}; doing so clobbers per-node absorption.");
            Assert.That(proposed.RxAdjRssi, Is.Not.Null, $"RxAdjRssiOptimizer should propose RxAdjRssi for {id}.");
        }
    }

    /// <summary>
    /// Builds a snapshot with one receiver that hears <paramref name="txCount"/> transmitters at
    /// increasing distances, with RSSI generated from a plausible log-distance model so the fit is
    /// well-posed (the optimizer requires >=3 transmitters per receiver).
    /// </summary>
    private static OptimizationSnapshot BuildSnapshotWithReceiver(string rxId, int txCount)
    {
        const double refRssi = -59;
        const double absorption = 3.0;

        var rx = new OptNode { Id = rxId, Name = rxId, Location = new Point3D(0, 0, 0) };
        var os = new OptimizationSnapshot();

        for (int i = 0; i < txCount; i++)
        {
            double distance = 2.0 + i * 2.0;
            var tx = new OptNode { Id = $"tx{i}", Name = $"tx{i}", Location = new Point3D(distance, 0, 0) };
            double rssi = refRssi - 10 * absorption * Math.Log10(distance);

            os.Measures.Add(new Measure
            {
                Rx = rx,
                Tx = tx,
                Rssi = rssi,
                RssiRxAdj = 0,
                RefRssi = refRssi,
                Distance = distance
            });
        }

        return os;
    }
}
