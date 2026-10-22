using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The joint optimizers used to store the global objective as every node's Error, so every node
/// reported the same number and a node whose links did not fit could not be told apart.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PerNodeErrorTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));
        _state.Config = new Config { Optimization = new ConfigOptimization { Enabled = true } };
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

    private IOptimizer Create(string mode) => mode switch
    {
        "per_node_absorption" => new PerNodeAbsorptionRxTx(_state),
        "global_absorption" => new GlobalAbsorptionRxTxOptimizer(_state),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>
    /// Five nodes that hear each other both ways, with RSSI from the log-distance model (txRef -59,
    /// absorption 3, rxAdj 0). Every link on <paramref name="badNode"/> is off by ±10 dB, alternating so
    /// no rxAdj, txRef or absorption can explain it away.
    /// </summary>
    private static OptimizationSnapshot Mesh(string? badNode)
    {
        var nodes = new[]
        {
            new OptNode { Id = "a", Name = "a", Location = new Point3D(0, 0, 1) },
            new OptNode { Id = "b", Name = "b", Location = new Point3D(8, 0, 1) },
            new OptNode { Id = "c", Name = "c", Location = new Point3D(8, 6, 1) },
            new OptNode { Id = "d", Name = "d", Location = new Point3D(0, 6, 1) },
            new OptNode { Id = "e", Name = "e", Location = new Point3D(3, 2, 1) },
        };

        var os = new OptimizationSnapshot();
        int k = 0;
        foreach (var rx in nodes)
        foreach (var tx in nodes)
        {
            if (rx == tx) continue;
            double d = rx.Location.DistanceTo(tx.Location);
            double offset = rx.Id == badNode || tx.Id == badNode ? (k++ % 2 == 0 ? 10 : -10) : 0;
            os.Measures.Add(new Measure
            {
                Rx = rx,
                Tx = tx,
                Rssi = -59 - 10 * 3.0 * Math.Log10(d) + offset,
                RssiRxAdj = 0,
                RefRssi = -59,
                Distance = d
            });
        }
        return os;
    }

    [TestCase("per_node_absorption")]
    [TestCase("global_absorption")]
    public void Optimize_ErrorSinglesOutTheNodeWhoseLinksDoNotFit(string mode)
    {
        var results = Create(mode).Optimize(Mesh("e"), new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes.Keys, Is.EquivalentTo(new[] { "a", "b", "c", "d", "e" }));
        var errors = results.Nodes.ToDictionary(kv => kv.Key, kv => kv.Value.Error);
        Assert.That(errors.Values, Has.All.Not.Null.And.All.GreaterThanOrEqualTo(0));

        // The global fit's quadratic, asymmetric objective bends the shared absorption toward e's links,
        // so the others carry some of its error; e must still be the node that stands out.
        foreach (var id in new[] { "a", "b", "c", "d" })
            Assert.That(errors["e"], Is.GreaterThan(1.25 * errors[id]!.Value), $"e should stand out from {id}: {string.Join(", ", errors)}");
    }

    [TestCase("per_node_absorption")]
    [TestCase("global_absorption")]
    public void Optimize_ErrorIsTheNodesRmsResidualInDb(string mode)
    {
        // Clean data fits to within rounding, so every node's residual is near 0 dB whatever the
        // objective's priors or penalties add up to.
        var clean = Create(mode).Optimize(Mesh(null), new Dictionary<string, NodeSettings>());
        Assert.That(clean.Nodes.Values.Select(n => n.Error), Has.All.Not.Null.And.All.LessThan(1.0));

        // Every link on e is exactly 10 dB off, and e's fit cannot absorb an alternating error, so its
        // RMS residual is on that scale rather than the scale of a squared or averaged objective.
        var noisy = Create(mode).Optimize(Mesh("e"), new Dictionary<string, NodeSettings>());
        Assert.That(noisy.Nodes["e"].Error, Is.InRange(5.0, 12.0));
    }
}
