using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class BaseMultilateralizerTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;

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
        if (_configLoader != null)
        {
            await _configLoader.StopAsync(CancellationToken.None);
            _configLoader.Dispose();
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

    private TestMultilateralizer CreateTestMultilateralizer(Device device, Floor floor)
    {
        return new TestMultilateralizer(device, floor, _state);
    }

    [Test]
    public void InitializeScenario_HandlesEmptyNodesArray()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Act
        var result = multilateralizer.PublicInitializeScenario(scenario, out var nodes, out var guess);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(nodes.Length, Is.EqualTo(0));
        Assert.That(double.IsNaN(guess.X), Is.True);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
    }

    [Test]
    public void InitializeScenario_HandlesSingleNode()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var node = CreateTestNode("node1", 1, 1, 0, floor);

        device.Nodes["node1"] = new DeviceToNode(device, node)
        {
            Distance = 1.0,
            LastHit = DateTime.UtcNow
        };

        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Act
        var result = multilateralizer.PublicInitializeScenario(scenario, out var nodes, out var guess);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(nodes.Length, Is.EqualTo(1));
        Assert.That(double.IsNaN(guess.X), Is.True);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
    }

    [Test]
    public void ClampToFloorBounds_HandlesNullBounds()
    {
        // Arrange
        var floor = new Floor();
        var configFloor = new ConfigFloor { Id = "unbounded_floor", Name = "Unbounded Floor" };
        floor.Update(_configLoader.Config!, configFloor);

        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var multilateralizer = CreateTestMultilateralizer(device, floor);
        var point = new Point3D(100, 200, 300);

        // Act
        var result = multilateralizer.PublicClampToFloorBounds(point);

        // Assert - point should be unchanged when no bounds
        Assert.That(result.X, Is.EqualTo(100));
        Assert.That(result.Y, Is.EqualTo(200));
        Assert.That(result.Z, Is.EqualTo(300));
    }

    [Test]
    public void ClampToFloorBounds_ClampsOutOfBoundsPoints()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Test point outside bounds (-10 to 10)
        var point = new Point3D(100, -200, 15);

        // Act
        var result = multilateralizer.PublicClampToFloorBounds(point);

        // Assert
        Assert.That(result.X, Is.EqualTo(10));   // Clamped to max
        Assert.That(result.Y, Is.EqualTo(-10));  // Clamped to min
        Assert.That(result.Z, Is.EqualTo(10));   // Clamped to max
    }

    [Test]
    public void CalculateAndSetPearsonCorrelation_HandlesLessThanTwoNodes()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        var nodes = new DeviceToNode[1];
        var node = CreateTestNode("node1", 1, 1, 0, floor);
        nodes[0] = new DeviceToNode(device, node) { Distance = 1.0 };

        // Act
        multilateralizer.PublicCalculateAndSetPearsonCorrelation(scenario, nodes);

        // Assert
        Assert.That(scenario.PearsonCorrelation, Is.Null);
    }

    [Test]
    public void HandleLocatorException_HandlesNaNGuess()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        var exception = new Exception("Test exception");
        var nanGuess = new Point3D(double.NaN, double.NaN, double.NaN);

        // Act
        var confidence = multilateralizer.PublicHandleLocatorException(exception, scenario, nanGuess);

        // Assert
        Assert.That(confidence, Is.EqualTo(0));
        // Should fallback to Point3D() which is (0, 0, 0)
        Assert.That(scenario.Location.X, Is.EqualTo(0));
        Assert.That(scenario.Location.Y, Is.EqualTo(0));
        Assert.That(scenario.Location.Z, Is.EqualTo(0));
    }

    [Test]
    public void FinalizeScenario_ReturnsFalseWhenNotMoved()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Set scenario location
        scenario.ResetLocation(new Point3D(5, 5, 0));
        scenario.LastLocation = new Point3D(5.05, 5.05, 0); // Less than 0.1 distance

        // Act
        var result = multilateralizer.PublicFinalizeScenario(scenario, 50);

        // Assert
        Assert.That(result, Is.False); // Should return false because movement < 0.1
    }

    // Test multilateralizer that exposes protected methods for testing
    private class TestMultilateralizer : BaseMultilateralizer
    {
        public TestMultilateralizer(Device device, Floor floor, State state)
            : base(device, floor, state)
        {
        }

        public override bool Locate(Scenario scenario)
        {
            return true;
        }

        public bool PublicInitializeScenario(Scenario scenario, out DeviceToNode[] nodes, out Point3D guess)
        {
            return InitializeScenario(scenario, out nodes, out guess);
        }

        public Point3D PublicClampToFloorBounds(Point3D point)
        {
            return ClampToFloorBounds(point);
        }

        public void PublicCalculateAndSetPearsonCorrelation(Scenario scenario, DeviceToNode[] nodes)
        {
            CalculateAndSetPearsonCorrelation(scenario, nodes);
        }

        public int PublicHandleLocatorException(Exception ex, Scenario scenario, Point3D fallbackGuess)
        {
            return HandleLocatorException(ex, scenario, fallbackGuess);
        }

        public bool PublicFinalizeScenario(Scenario scenario, int confidence)
        {
            return FinalizeScenario(scenario, confidence);
        }
    }
}
