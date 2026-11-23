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
    public async Task Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        // Without a config file the loader installs the example, whose floors would mix with the test's
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), "floors: []\n");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.LoadAsync(); // the constructor no longer loads
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
        // Four zero-range nodes at one spot pin the solve there for these floor-assignment tests.
        var node = CreateTestNode("node1", "Node 1", location, floors);
        var device = CreateDeviceWithNode(node, 0.0);
        for (var i = 2; i <= 4; i++)
        {
            var other = CreateTestNode($"node{i}", $"Node {i}", location, floors);
            device.Nodes[other.Id] = new DeviceToNode(device, other) { Distance = 0.0, LastHit = DateTime.UtcNow };
        }
        var locator = new MultiFloorMultilateralizer(device, _state);
        // Confidence starts low so the solve is seeded at the nodes, not the origin
        var scenario = new Scenario(_configLoader.Config, locator, "MultiFloor") { Confidence = 0 };
        return (locator.Locate(scenario), scenario);
    }

    // The first fix seeds the Kalman filter, which then only creeps towards later ones, so a fix
    // seeded from the origin shows every device starting at 0,0,0 and sliding into place
    [TestCase(5)]
    [TestCase(4)]
    public void Locate_FirstFix_DoesNotStartFromTheOrigin(int heard)
    {
        var floor = CreateTestFloor("first", new double[] { -20, -20, 0 }, new double[] { 40, 40, 3 });
        var truth = new Point3D(14, 9, 1);
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var at = new[] { new Point3D(12, 7, 1), new Point3D(17, 8, 2), new Point3D(13, 13, 1), new Point3D(19, 12, 1.5), new Point3D(10, 10, 0.5) };
        foreach (var (location, i) in at.Take(heard).Select((l, i) => (l, i)))
        {
            var node = CreateTestNode($"n{i}", $"n{i}", location, floor);
            device.Nodes[node.Id] = new DeviceToNode(device, node) { Distance = truth.DistanceTo(location), LastHit = DateTime.UtcNow };
        }
        var scenario = new Scenario(_configLoader.Config, new MultiFloorMultilateralizer(device, _state), "MultiFloor");

        scenario.Locate();

        Assert.That(scenario.Location.DistanceTo(truth), Is.LessThan(0.2));
        Assert.That(scenario.Scale, Is.EqualTo(1).Within(0.01));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Locate_FewerThanFourNodes_PreservesLastPositionWithoutANewFix(int count)
    {
        var (ground, upstairs) = CreateTwoFloors();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var lastHit = DateTime.UtcNow.AddSeconds(-1);
        for (var i = 0; i < count; i++)
        {
            var node = CreateTestNode($"node{i}", null, new Point3D(i, i, 1), ground, upstairs);
            device.Nodes[node.Id] = new DeviceToNode(device, node) { Distance = 40, LastHit = lastHit };
        }
        var locator = new MultiFloorMultilateralizer(device, _state);
        var room = ground.Rooms.Values.Single();
        var scenario = new Scenario(_configLoader.Config, locator, "MultiFloor") { Confidence = 100, Floor = ground, Room = room };
        var previous = new Point3D(2, 2, 1);
        scenario.ResetLocation(previous);

        var moved = locator.Locate(scenario);

        Assert.That(moved, Is.False);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.SameAs(room));
        Assert.That(scenario.Floor, Is.SameAs(ground));
        Assert.That(scenario.Location, Is.EqualTo(previous));
        Assert.That(scenario.Fixes, Is.EqualTo(count));
        Assert.That(scenario.LastHit, Is.EqualTo(count == 0 ? (DateTime?)null : lastHit));
        Assert.That(scenario.Minimum, Is.EqualTo(count == 0 ? (double?)null : 40));
        Assert.That(scenario.Trace!.Outcome, Is.EqualTo(LocatorOutcome.NoFix));
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
