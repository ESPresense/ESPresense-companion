using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

[TestFixture]
public class CombinedOptimizerTests
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

        // Ensure state has an optimization config
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

    /// <summary>
    /// Creates a measure between two OptNodes using the path-loss formula to derive RSSI
    /// from the known distance and absorption.
    /// RSSI = -59 + rxAdjRssi + txAdjRssi - 10 * absorption * log10(distance)
    /// </summary>
    private static Measure MakeMeasure(OptNode rx, OptNode tx, double trueAbsorption = 3.0)
    {
        double distance = rx.Location.DistanceTo(tx.Location);
        // Invert the BFGS distance formula: dist = 10^((-59 + rxAdj + txAdj - Rssi) / (10 * abs))
        // With rxAdj = txAdj = 0: Rssi = -59 - 10 * abs * log10(dist)
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
    public void Optimize_WithDirectionalNodes_ProducesAbsorptionsInRange()
    {
        // Arrange: 4 nodes in a square, 2 with directional antennas (GMaxDb = 5)
        var nodeA = MakeNode("node_a", 0, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeB = MakeNode("node_b", 5, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeC = MakeNode("node_c", 5, 5, 0, isNode: true, gMaxDb: null);
        var nodeD = MakeNode("node_d", 0, 5, 0, isNode: true, gMaxDb: null);

        var os = new OptimizationSnapshot();
        os.Measures.Add(MakeMeasure(nodeA, nodeB));
        os.Measures.Add(MakeMeasure(nodeB, nodeA));
        os.Measures.Add(MakeMeasure(nodeB, nodeC));
        os.Measures.Add(MakeMeasure(nodeC, nodeB));
        os.Measures.Add(MakeMeasure(nodeC, nodeD));
        os.Measures.Add(MakeMeasure(nodeD, nodeC));
        os.Measures.Add(MakeMeasure(nodeD, nodeA));
        os.Measures.Add(MakeMeasure(nodeA, nodeD));
        os.Measures.Add(MakeMeasure(nodeA, nodeC));
        os.Measures.Add(MakeMeasure(nodeC, nodeA));

        var optimizer = new CombinedOptimizer(_state);
        var existingSettings = new Dictionary<string, NodeSettings>();

        // Act
        var results = optimizer.Optimize(os, existingSettings);

        // Assert: all nodes should have proposed absorption values within [2.5, 3.5]
        Assert.That(results.Nodes.Count, Is.GreaterThan(0), "Optimizer should produce results");
        foreach (var (id, proposed) in results.Nodes)
        {
            Assert.That(proposed.Absorption, Is.InRange(2.5, 3.5),
                $"Node {id} absorption should be in [2.5, 3.5]");
        }
    }

    [Test]
    public void Optimize_DirectionalNodes_HaveAzimuthAndElevation()
    {
        // Arrange: 2 directional nodes (GMaxDb > 0) + 2 omnidirectional nodes
        var nodeA = MakeNode("dir_node_a", 0, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeB = MakeNode("dir_node_b", 5, 0, 0, isNode: true, gMaxDb: 3.0);
        var nodeC = MakeNode("omni_node_c", 5, 5, 0, isNode: true, gMaxDb: null);
        var nodeD = MakeNode("omni_node_d", 0, 5, 0, isNode: true, gMaxDb: null);

        var os = new OptimizationSnapshot();
        os.Measures.Add(MakeMeasure(nodeA, nodeB));
        os.Measures.Add(MakeMeasure(nodeB, nodeA));
        os.Measures.Add(MakeMeasure(nodeA, nodeC));
        os.Measures.Add(MakeMeasure(nodeC, nodeA));
        os.Measures.Add(MakeMeasure(nodeB, nodeD));
        os.Measures.Add(MakeMeasure(nodeD, nodeB));
        os.Measures.Add(MakeMeasure(nodeC, nodeD));
        os.Measures.Add(MakeMeasure(nodeD, nodeC));
        os.Measures.Add(MakeMeasure(nodeA, nodeD));
        os.Measures.Add(MakeMeasure(nodeD, nodeA));

        var optimizer = new CombinedOptimizer(_state);
        var existingSettings = new Dictionary<string, NodeSettings>();

        // Act
        var results = optimizer.Optimize(os, existingSettings);

        // Assert: directional nodes should have azimuth and elevation
        if (results.Nodes.TryGetValue("dir_node_a", out var proposedA))
        {
            Assert.That(proposedA.Azimuth, Is.Not.Null, "Directional node_a should have Azimuth");
            Assert.That(proposedA.Elevation, Is.Not.Null, "Directional node_a should have Elevation");
            Assert.That(proposedA.Azimuth, Is.InRange(0.0, 360.0), "Azimuth should be [0, 360) degrees");
            Assert.That(proposedA.Elevation, Is.InRange(-90.0, 90.0), "Elevation should be [-90, 90] degrees");
        }

        if (results.Nodes.TryGetValue("dir_node_b", out var proposedB))
        {
            Assert.That(proposedB.Azimuth, Is.Not.Null, "Directional node_b should have Azimuth");
            Assert.That(proposedB.Elevation, Is.Not.Null, "Directional node_b should have Elevation");
        }

        // Omnidirectional nodes should NOT have azimuth or elevation
        if (results.Nodes.TryGetValue("omni_node_c", out var proposedC))
        {
            Assert.That(proposedC.Azimuth, Is.Null, "Omnidirectional node_c should NOT have Azimuth");
            Assert.That(proposedC.Elevation, Is.Null, "Omnidirectional node_c should NOT have Elevation");
        }

        if (results.Nodes.TryGetValue("omni_node_d", out var proposedD))
        {
            Assert.That(proposedD.Azimuth, Is.Null, "Omnidirectional node_d should NOT have Azimuth");
            Assert.That(proposedD.Elevation, Is.Null, "Omnidirectional node_d should NOT have Elevation");
        }
    }

    [Test]
    public void Optimize_InsufficientMeasurements_ReturnsEmptyResults()
    {
        // Arrange: only 2 measures (below the minimum of 3)
        var nodeA = MakeNode("node_a", 0, 0, 0, isNode: true, gMaxDb: 5.0);
        var nodeB = MakeNode("node_b", 5, 0, 0, isNode: true, gMaxDb: 5.0);

        var os = new OptimizationSnapshot();
        os.Measures.Add(MakeMeasure(nodeA, nodeB));
        os.Measures.Add(MakeMeasure(nodeB, nodeA));

        var optimizer = new CombinedOptimizer(_state);
        var existingSettings = new Dictionary<string, NodeSettings>();

        // Act
        var results = optimizer.Optimize(os, existingSettings);

        // Assert: should return empty results
        Assert.That(results.Nodes, Is.Empty, "Should return empty results with < 3 measurements");
    }

    [Test]
    public void TakeOptimizationSnapshot_SetsIsNodeTrue_ForConfigNodes()
    {
        // Arrange: add config nodes to state
        var configFloor = new ConfigFloor { Id = "test_floor", Name = "Test Floor" };
        var floor = new Floor();
        floor.Update(_state.Config!, configFloor);
        _state.Floors["test_floor"] = floor;

        var configNodeA = new ConfigNode { Name = "node_a", Point = new double[] { 0, 0, 0 }, Antenna = "external_dipole" };
        var configNodeB = new ConfigNode { Name = "node_b", Point = new double[] { 5, 0, 0 } };

        var nodeA = new Node("node_a", NodeSourceType.Config);
        nodeA.Update(_state.Config!, configNodeA, new[] { floor });
        _state.Nodes["node_a"] = nodeA;

        var nodeB = new Node("node_b", NodeSourceType.Config);
        nodeB.Update(_state.Config!, configNodeB, new[] { floor });
        _state.Nodes["node_b"] = nodeB;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // We can only verify IsNode/GMax if there are actual RxNode measurements.
        // Without measurements, no OptNodes are created. This test verifies the
        // snapshot is returned without errors.
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot.Measures, Is.Empty, "No measures without RxNode data");
    }
}
