using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// Regression test for the per-node Error reporting fix. The optimizer used to store the single
/// global objective value on every node, so every node's Error was identical regardless of how
/// well that node actually fit. Each node's Error should now reflect its own residuals.
/// </summary>
public class PerNodeAbsorptionRxTxTests
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
        // Config loads asynchronously via the file watcher; set it explicitly so the optimizer
        // has its bounds (defaults: absorption [2,5], rxAdj [-5,30], txRef [-70,-50]).
        _state.Config = new Config();
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
                try { Directory.Delete(_configDir, recursive: true); break; }
                catch (IOException) when (i < 4) { await Task.Delay(100); GC.Collect(); GC.WaitForPendingFinalizers(); }
            }
        }
    }

    [Test]
    public void Optimize_ReportsDistinctPerNodeError()
    {
        // Two receivers hearing the same transmitters. rxGood's RSSI matches the log-distance
        // model; rxBad's RSSI is inconsistent with geometry, so its residuals must be larger.
        const double refRssi = -59;
        const double absorption = 3.0;

        // Separate transmitters per receiver so their fits don't couple through a shared txRef.
        var goodTx = new[]
        {
            new OptNode { Id = "txA", Name = "txA", Location = new Point3D(0, 0, 0) },
            new OptNode { Id = "txB", Name = "txB", Location = new Point3D(10, 0, 0) },
            new OptNode { Id = "txC", Name = "txC", Location = new Point3D(0, 10, 0) }
        };
        var badTx = new[]
        {
            new OptNode { Id = "txD", Name = "txD", Location = new Point3D(20, 0, 0) },
            new OptNode { Id = "txE", Name = "txE", Location = new Point3D(0, 20, 0) },
            new OptNode { Id = "txF", Name = "txF", Location = new Point3D(20, 20, 0) }
        };

        var rxGood = new OptNode { Id = "rxGood", Name = "rxGood", Location = new Point3D(3, 4, 0) };
        var rxBad = new OptNode { Id = "rxBad", Name = "rxBad", Location = new Point3D(16, 8, 0) };

        var os = new OptimizationSnapshot();

        foreach (var tx in goodTx)
        {
            double d = rxGood.Location.DistanceTo(tx.Location);
            os.Measures.Add(new Measure
            {
                Rx = rxGood, Tx = tx, RssiRxAdj = 0, RefRssi = refRssi, Distance = d,
                Rssi = refRssi - 10 * absorption * Math.Log10(d) // consistent with model
            });
        }

        // Alternating-sign offsets: no single rxAdj/absorption/txRef can satisfy all three, so
        // rxBad carries an irreducible residual that the well-fitting rxGood does not.
        double[] offsets = { +12, -12, +12 };
        for (int i = 0; i < badTx.Length; i++)
        {
            double d = rxBad.Location.DistanceTo(badTx[i].Location);
            os.Measures.Add(new Measure
            {
                Rx = rxBad, Tx = badTx[i], RssiRxAdj = 0, RefRssi = refRssi, Distance = d,
                Rssi = refRssi - 10 * absorption * Math.Log10(d) + offsets[i]
            });
        }

        var optimizer = new PerNodeAbsorptionRxTx(_state);
        var results = optimizer.Optimize(os, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes.ContainsKey("rxGood"), Is.True);
        Assert.That(results.Nodes.ContainsKey("rxBad"), Is.True);

        var errGood = results.Nodes["rxGood"].Error;
        var errBad = results.Nodes["rxBad"].Error;

        Assert.That(errGood, Is.Not.Null);
        Assert.That(errBad, Is.Not.Null);
        Assert.That(errGood, Is.Not.EqualTo(errBad),
            "Per-node Error must be node-specific, not the shared global objective value.");
        Assert.That(errBad!.Value, Is.GreaterThan(errGood!.Value),
            "The receiver whose RSSI contradicts geometry should report the larger per-node error.");
    }
}
