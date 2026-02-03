using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class NearestNodeTests
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

    [Test]
    public void Locate_PicksFloorContainingLocationAndRoomByPolygon()
    {
        // Arrange
        var outsideFloor = CreateTestFloor(
            "outside",
            new[] { 10.0, 10.0, 10.0 },
            new[] { 20.0, 20.0, 20.0 });

        var room = CreateRoom(
            "Office",
            new[]
            {
                new[] { 0.0, 0.0 },
                new[] { 2.0, 0.0 },
                new[] { 2.0, 2.0 },
                new[] { 0.0, 2.0 }
            });

        var insideFloor = CreateTestFloor(
            "inside",
            new[] { -5.0, -5.0, -1.0 },
            new[] { 5.0, 5.0, 1.0 },
            room);

        var location = new Point3D(1.0, 1.0, 0.0);
        var node = CreateTestNode("node1", "Node 1", location, outsideFloor, insideFloor);
        var device = CreateDeviceWithNode(node, 1.0);

        var locator = new NearestNode(device, _state);
        var scenario = new Scenario(_configLoader.Config, locator, "Nearest");

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(moved, Is.True);
        Assert.That(scenario.Location, Is.EqualTo(location));
        Assert.That(scenario.Confidence, Is.EqualTo(1));
        Assert.That(scenario.Fixes, Is.EqualTo(1));
        Assert.That(scenario.Floor, Is.EqualTo(insideFloor));
        Assert.That(scenario.Room, Is.EqualTo(insideFloor.Rooms.Values.First()));
    }

    [Test]
    public void Locate_FallsBackToRoomNameWhenNoRoomContainsLocation()
    {
        // Arrange
        var room = CreateRoom(
            "KITCHEN",
            new[]
            {
                new[] { 100.0, 100.0 },
                new[] { 101.0, 100.0 },
                new[] { 101.0, 101.0 },
                new[] { 100.0, 101.0 }
            });

        var floor = CreateTestFloor(
            "main",
            new[] { -5.0, -5.0, -1.0 },
            new[] { 5.0, 5.0, 1.0 },
            room);

        var location = new Point3D(1.0, 1.0, 0.0);
        var node = CreateTestNode("kitchen", "Kitchen Node", location, floor);
        var device = CreateDeviceWithNode(node, 1.0);

        var locator = new NearestNode(device, _state);
        var scenario = new Scenario(_configLoader.Config, locator, "Nearest");

        // Act
        var moved = locator.Locate(scenario);

        // Assert
        Assert.That(moved, Is.True);
        Assert.That(scenario.Floor, Is.EqualTo(floor));
        Assert.That(scenario.Room, Is.EqualTo(floor.Rooms.Values.First()));
    }
}
