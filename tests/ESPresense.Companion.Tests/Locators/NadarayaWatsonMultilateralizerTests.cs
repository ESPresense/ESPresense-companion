using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class NadarayaWatsonMultilateralizerTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<NodeTelemetryStore> _mockNodeTelemetryStore;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _mockNodeTelemetryStore = new Mock<NodeTelemetryStore>(_mockMqttCoordinator.Object);
        _mockNodeTelemetryStore.Setup(n => n.Online(It.IsAny<string>())).Returns(true);
        _state = new State(_configLoader, _nodeTelemetryStore);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_configLoader != null)
        {
            await _configLoader.StopAsync(CancellationToken.None);
            _configLoader.Dispose();
        }

        if (_nodeTelemetryStore != null)
        {
            await _nodeTelemetryStore.StopAsync(CancellationToken.None);
            _nodeTelemetryStore.Dispose();
        }

        if (Directory.Exists(_configDir))
        {
            try
            {
                Directory.Delete(_configDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private Floor CreateTestFloor(string id = "test_floor")
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Id = id,
            Name = "Test Floor",
            Bounds = new double[][]
            {
                new double[] { -10, -10, -10 },
                new double[] { 10, 10, 10 }
            }
        };
        floor.Update(_configLoader.Config!, configFloor);
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    private Node CreateTestNode(string id, double x, double y, double z, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        var configNode = new ConfigNode { Name = id, Point = new double[] { x, y, z } };
        node.Update(_configLoader.Config!, configNode, new[] { floor });
        _state.Nodes[id] = node;
        return node;
    }

    [Test]
    public void Locate_WithThreeNodes_CalculatesPosition()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);

        var expectedPosition = new Point3D(2.5, 2, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(scenario.Confidence, Is.GreaterThan(0));
        Assert.That(scenario.Location.DistanceTo(expectedPosition), Is.LessThan(1.0));
        Assert.That(scenario.Fixes, Is.EqualTo(3));
    }

    [Test]
    public void Locate_WithOneNode_ReturnsFalse()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = 2.0,
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Error, Is.Null);
    }

    [Test]
    public void Locate_WithTwoNodes_UsesMidpoint()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 6, 0, 0, floor);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = 3.0,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = 3.0,
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert - with 2 nodes and no bounds, uses midpoint
        Assert.That(scenario.Error, Is.Null);
        Assert.That(scenario.PearsonCorrelation, Is.Not.Null);
        var expectedMidpoint = Point3D.MidPoint(node1.Location, node2.Location);
        Assert.That(scenario.Location.DistanceTo(expectedMidpoint), Is.LessThan(0.01));
    }

    [Test]
    public void Locate_SetsConfidenceCorrectly()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);
        var node4 = CreateTestNode("node4", 2.5, -4.33, 0, floor);

        var expectedPosition = new Point3D(2.5, 0, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node4"] = new DeviceToNode(device, node4)
        {
            Distance = expectedPosition.DistanceTo(node4.Location),
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(scenario.Confidence, Is.GreaterThanOrEqualTo(5));
        Assert.That(scenario.Confidence, Is.LessThanOrEqualTo(100));
    }

    [Test]
    public void Locate_CalculatesPearsonCorrelation()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);

        device.Nodes["node1"] = new DeviceToNode(device, node1) { Distance = 2.5, LastHit = DateTime.UtcNow };
        device.Nodes["node2"] = new DeviceToNode(device, node2) { Distance = 2.5, LastHit = DateTime.UtcNow };
        device.Nodes["node3"] = new DeviceToNode(device, node3) { Distance = 2.5, LastHit = DateTime.UtcNow };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(scenario.PearsonCorrelation, Is.Not.Null);
    }

    [Test]
    public void Locate_UsesCentralizedConfidence()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);

        var expectedPosition = new Point3D(2.5, 2, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        multilateralizer.Locate(scenario);

        // Assert - centralized confidence is clamped between ConfidenceFloor (5) and 100
        Assert.That(scenario.Confidence, Is.GreaterThanOrEqualTo(5));
        Assert.That(scenario.Confidence, Is.LessThanOrEqualTo(100));
    }

    [Test]
    public void Locate_UsesInverseDistanceSquaredWeighting()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        // Place nodes so that closer node should dominate position
        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 10, 0, 0, floor);
        var node3 = CreateTestNode("node3", 5, 8.66, 0, floor);

        // Device is very close to node1
        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = 1.0,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = 9.0,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = 9.0,
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert - position should be much closer to node1 due to inverse-distance-squared weighting
        Assert.That(result, Is.True);
        Assert.That(scenario.Location.DistanceTo(node1.Location), Is.LessThan(3.0));
        Assert.That(scenario.Location.DistanceTo(node2.Location), Is.GreaterThan(5.0));
    }

    [Test]
    public void Locate_CalculatesWeightedMSE()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);

        var expectedPosition = new Point3D(2.5, 2, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert - error should be set and non-negative
        Assert.That(result, Is.True);
        Assert.That(scenario.Error, Is.Not.Null);
        Assert.That(scenario.Error, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Locate_ConsidersOnlineNodesForConfidence()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);
        // Add extra nodes that are online but not hearing the device
        CreateTestNode("node4", -5, 0, 0, floor);
        CreateTestNode("node5", 0, -5, 0, floor);

        var expectedPosition = new Point3D(2.5, 2, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };

        // Mock: all 5 nodes are online, but only 3 hear the device
        var mockNts = new Mock<NodeTelemetryStore>(_mockMqttCoordinator.Object);
        mockNts.Setup(n => n.Online(It.IsAny<string>())).Returns(true);

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, mockNts.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert - confidence should account for 3/5 coverage ratio
        Assert.That(result, Is.True);
        Assert.That(scenario.Confidence, Is.GreaterThanOrEqualTo(5));
        Assert.That(scenario.Confidence, Is.LessThanOrEqualTo(100));
    }

    [Test]
    public void Locate_AssignsRoomWhenInsidePolygon()
    {
        // Arrange
        var floor = CreateTestFloor();

        var room = new Room();
        var configRoom = new ConfigRoom
        {
            Id = "test_room",
            Name = "Test Room",
            Points = new double[][]
            {
                new double[] { 0, 0 },
                new double[] { 5, 0 },
                new double[] { 5, 5 },
                new double[] { 0, 5 }
            }
        };
        room.Update(_configLoader.Config!, floor, configRoom);
        floor.Rooms[room.Id] = room;

        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));

        var node1 = CreateTestNode("node1", 0, 0, 0, floor);
        var node2 = CreateTestNode("node2", 5, 0, 0, floor);
        var node3 = CreateTestNode("node3", 2.5, 4.33, 0, floor);

        var expectedPosition = new Point3D(2.5, 2.5, 0);

        device.Nodes["node1"] = new DeviceToNode(device, node1)
        {
            Distance = expectedPosition.DistanceTo(node1.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = new DeviceToNode(device, node2)
        {
            Distance = expectedPosition.DistanceTo(node2.Location),
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node3"] = new DeviceToNode(device, node3)
        {
            Distance = expectedPosition.DistanceTo(node3.Location),
            LastHit = DateTime.UtcNow
        };

        var multilateralizer = new NadarayaWatsonMultilateralizer(device, floor, _state, _mockNodeTelemetryStore.Object);
        var scenario = new Scenario(_configLoader.Config, multilateralizer, "NadarayaWatson");

        // Act
        var result = multilateralizer.Locate(scenario);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(scenario.Room, Is.EqualTo(room));
    }
}
