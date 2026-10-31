using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class MultiFloorMultilateralizerTests
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

    private Floor CreateTestFloor(string id, double[] min, double[] max, params ConfigRoom[] rooms)
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Id = id,
            Name = id,
            Bounds = new[] { min, max },
            Rooms = rooms
        };

        floor.Update(_configLoader.Config!, configFloor);
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    private static ConfigRoom CreateRoom(string name, double[][] points)
    {
        return new ConfigRoom
        {
            Name = name,
            Points = points
        };
    }

    private Node CreateTestNode(string id, string? name, Point3D location, params Floor[] floors)
    {
        var node = new Node(id, NodeSourceType.Config);
        var configNode = new ConfigNode
        {
            Id = id,
            Name = name,
            Point = new[] { location.X, location.Y, location.Z }
        };

        node.Update(_configLoader.Config!, configNode, floors);
        _state.Nodes[id] = node;
        return node;
    }

    private static Device CreateDeviceWithNode(Node node, double distance)
    {
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        device.Nodes[node.Id] = new DeviceToNode(device, node)
        {
            Distance = distance,
            LastHit = DateTime.UtcNow
        };

        return device;
    }

    private static readonly double[][] Square = { new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 4.0 }, new[] { 0.0, 4.0 } };

    private (Floor ground, Floor upstairs) CreateTwoFloors()
    {
        var ground = CreateTestFloor("ground", new[] { -1.0, -1.0, 0.0 }, new[] { 5.0, 5.0, 3.0 }, CreateRoom("Kitchen", Square));
        var upstairs = CreateTestFloor("upstairs", new[] { -1.0, -1.0, 3.0 }, new[] { 5.0, 5.0, 6.0 }, CreateRoom("Bedroom", Square));
        return (ground, upstairs);
    }

    private (bool moved, Scenario scenario) LocateAt(Point3D location, params Floor[] floors)
    {
        var node = CreateTestNode("node1", "Node 1", location, floors);
        var device = CreateDeviceWithNode(node, 1.0);
        var locator = new MultiFloorMultilateralizer(device, _state);
        // Confidence starts low so the single-node fallback seeds the solve at the node, not the origin
        var scenario = new Scenario(_configLoader.Config, locator, "MultiFloor") { Confidence = 0 };
        return (locator.Locate(scenario), scenario);
    }

    [Test]
    public void Locate_AssignsFloorOfContainingRoom()
    {
        var (ground, upstairs) = CreateTwoFloors();

        var (moved, scenario) = LocateAt(new Point3D(2, 2, 4.5), ground, upstairs);

        Assert.That(moved, Is.True);
        Assert.That(scenario.Floor, Is.EqualTo(upstairs));
        Assert.That(scenario.Room?.Name, Is.EqualTo("Bedroom"));
    }

    [Test]
    public void Locate_AssignsFloorEvenOutsideAllRooms()
    {
        var (ground, upstairs) = CreateTwoFloors();

        var (_, scenario) = LocateAt(new Point3D(-0.5, -0.5, 1.0), ground, upstairs);

        Assert.That(scenario.Floor, Is.EqualTo(ground));
        Assert.That(scenario.Room, Is.Null);
    }

    [Test]
    public void Locate_BelowLowestFloor_SnapsToNearestFloor()
    {
        var (ground, upstairs) = CreateTwoFloors();

        var (_, scenario) = LocateAt(new Point3D(2, 2, -0.8), ground, upstairs);

        Assert.That(scenario.Floor, Is.EqualTo(ground));
        Assert.That(scenario.Room?.Name, Is.EqualTo("Kitchen"));
    }

    [Test]
    public void Locate_AboveHighestFloor_SnapsToNearestFloor()
    {
        var (ground, upstairs) = CreateTwoFloors();

        var (_, scenario) = LocateAt(new Point3D(2, 2, 7.0), ground, upstairs);

        Assert.That(scenario.Floor, Is.EqualTo(upstairs));
        Assert.That(scenario.Room?.Name, Is.EqualTo("Bedroom"));
    }
}
