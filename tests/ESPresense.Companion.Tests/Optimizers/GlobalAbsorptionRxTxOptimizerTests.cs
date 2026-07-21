using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

[TestFixture]
public class GlobalAbsorptionRxTxOptimizerTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<NodeSettingsStore> _mockNss;
    private Mock<DeviceSettingsStore> _mockDss;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        var nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _mockNss = new Mock<NodeSettingsStore>(_mockMqttCoordinator.Object, (ILogger<NodeSettingsStore>)null!);
        _mockDss = new Mock<DeviceSettingsStore>(_mockMqttCoordinator.Object, (State)null!);
        var lazyDss = new Lazy<DeviceSettingsStore>(() => _mockDss.Object);
        _state = new State(_configLoader, nodeTelemetryStore, _mockNss.Object, lazyDss);

        _state.Config = new Config
        {
            Optimization = new ConfigOptimization
            {
                Enabled = true,
                Limits = new Dictionary<string, double>
                {
                    ["absorption_min"] = 2.5,
                    ["absorption_max"] = 3.5,
                    ["rx_adj_rssi_min"] = -5,
                    ["rx_adj_rssi_max"] = 30
                }
            }
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_configLoader != null)
        {
            await _configLoader.StopAsync(CancellationToken.None);
            _configLoader.Dispose();
        }

        if (Directory.Exists(_configDir))
        {
            try { Directory.Delete(_configDir, true); } catch { }
        }
    }

    private static Measure MakeMeasure(OptNode rx, OptNode tx, double trueAbsorption = 3.0)
    {
        double distance = rx.Location.DistanceTo(tx.Location);
        double rssi = -59.0 - 10.0 * trueAbsorption * Math.Log10(Math.Max(distance, 0.1));
        return new Measure
        {
            Rx = rx,
            Tx = tx,
            Rssi = rssi,
            RefRssi = -59.0,
            Distance = distance
        };
    }

    private static OptNode MakeNode(string id, double x, double y, double z, bool isNode = true, double? gMaxDb = null)
    {
        double gMax = gMaxDb.HasValue ? Math.Pow(10.0, gMaxDb.Value / 10.0) : 1.0;
        bool hasDirectional = gMaxDb.HasValue;
        return new OptNode
        {
            Id = id,
            Name = id,
            Location = new Point3D(x, y, z),
            IsNode = isNode,
            HasDirectionalAntenna = hasDirectional,
            GMax = gMax
        };
    }

    [Test]
    public void Optimize_UsesOnlyRxAdjustmentAndNoTxRef()
    {
        var nodeA = MakeNode("node_a", 0, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeB = MakeNode("node_b", 5, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeC = MakeNode("node_c", 5, 5, 0, isNode: true, gMaxDb: null);
        var nodeD = MakeNode("node_d", 0, 5, 0, isNode: true, gMaxDb: null);

        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(MakeMeasure(nodeA, nodeB));
        snapshot.Measures.Add(MakeMeasure(nodeB, nodeA));
        snapshot.Measures.Add(MakeMeasure(nodeB, nodeC));
        snapshot.Measures.Add(MakeMeasure(nodeC, nodeB));
        snapshot.Measures.Add(MakeMeasure(nodeC, nodeD));
        snapshot.Measures.Add(MakeMeasure(nodeD, nodeC));
        snapshot.Measures.Add(MakeMeasure(nodeD, nodeA));
        snapshot.Measures.Add(MakeMeasure(nodeA, nodeD));

        var optimizer = new GlobalAbsorptionRxTxOptimizer(_state);
        var results = optimizer.Optimize(snapshot, new Dictionary<string, NodeSettings>());

        Assert.Multiple(() =>
        {
            Assert.That(results.Nodes, Contains.Key("node_a"));
            Assert.That(results.Nodes, Contains.Key("node_b"));
            Assert.That(results.Nodes, Contains.Key("node_c"));
            Assert.That(results.Nodes, Contains.Key("node_d"));
        });

        foreach (var (id, proposed) in results.Nodes)
        {
            Assert.That(proposed.Absorption, Is.InRange(2.5, 3.5), $"Node {id} absorption should be in range");
            Assert.That(proposed.RxAdjRssi, Is.Not.Null, $"Node {id} should have an Rx adjustment");
        }
    }

    [Test]
    public void Optimize_DirectionalNodes_StillProduceAngles()
    {
        var nodeA = MakeNode("dir_node_a", 0, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeB = MakeNode("dir_node_b", 5, 0, 0, isNode: true, gMaxDb: 3.0);
        var nodeC = MakeNode("omni_node_c", 5, 5, 0, isNode: true, gMaxDb: null);
        var nodeD = MakeNode("omni_node_d", 0, 5, 0, isNode: true, gMaxDb: null);

        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(MakeMeasure(nodeA, nodeB));
        snapshot.Measures.Add(MakeMeasure(nodeB, nodeA));
        snapshot.Measures.Add(MakeMeasure(nodeA, nodeC));
        snapshot.Measures.Add(MakeMeasure(nodeC, nodeA));
        snapshot.Measures.Add(MakeMeasure(nodeB, nodeD));
        snapshot.Measures.Add(MakeMeasure(nodeD, nodeB));
        snapshot.Measures.Add(MakeMeasure(nodeC, nodeD));
        snapshot.Measures.Add(MakeMeasure(nodeD, nodeC));

        var optimizer = new GlobalAbsorptionRxTxOptimizer(_state);
        var results = optimizer.Optimize(snapshot, new Dictionary<string, NodeSettings>());

        var proposedA = results.Nodes["dir_node_a"];
        var proposedB = results.Nodes["dir_node_b"];
        var proposedC = results.Nodes["omni_node_c"];
        var proposedD = results.Nodes["omni_node_d"];

        Assert.That(proposedA.Azimuth, Is.Not.Null);
        Assert.That(proposedA.Elevation, Is.Not.Null);
        Assert.That(proposedB.Azimuth, Is.Not.Null);
        Assert.That(proposedB.Elevation, Is.Not.Null);
        Assert.That(proposedC.Azimuth, Is.Null);
        Assert.That(proposedC.Elevation, Is.Null);
        Assert.That(proposedD.Azimuth, Is.Null);
        Assert.That(proposedD.Elevation, Is.Null);
    }
}
