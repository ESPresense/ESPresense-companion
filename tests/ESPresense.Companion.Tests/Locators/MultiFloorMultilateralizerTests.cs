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
        // Two zero-range nodes at one spot pin the solve there, whatever the floor layout
        var node = CreateTestNode("node1", "Node 1", location, floors);
        var device = CreateDeviceWithNode(node, 0.0);
        var other = CreateTestNode("node2", "Node 2", location, floors);
        device.Nodes[other.Id] = new DeviceToNode(device, other) { Distance = 0.0, LastHit = DateTime.UtcNow };
        var locator = new MultiFloorMultilateralizer(device, _state);
        // Confidence starts low so the solve is seeded at the nodes, not the origin
        var scenario = new Scenario(_configLoader.Config, locator, "MultiFloor") { Confidence = 0 };
        return (locator.Locate(scenario), scenario);
    }

    // The first fix seeds the Kalman filter, which then only creeps towards later ones, so a fix
    // seeded from the origin shows every device starting at 0,0,0 and sliding into place
    [TestCase(5)]
    [TestCase(3)]
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

        // Horizontal only: three nodes leave two exact answers, mirrored through their plane
        Assert.That(new Point2D(scenario.Location.X, scenario.Location.Y).DistanceTo(new Point2D(truth.X, truth.Y)), Is.LessThan(0.2));
        Assert.That(scenario.Scale, Is.EqualTo(1).Within(0.01));
    }

    [Test]
    public void Locate_SingleNode_ProducesNoFix()
    {
        var (ground, upstairs) = CreateTwoFloors();
        var node = CreateTestNode("node1", "Node 1", new Point3D(2, 2, 1), ground, upstairs);
        var device = CreateDeviceWithNode(node, 40.0);
        var locator = new MultiFloorMultilateralizer(device, _state);
        var scenario = new Scenario(_configLoader.Config, locator, "MultiFloor") { Confidence = 0 };

        var moved = locator.Locate(scenario);

        Assert.That(moved, Is.False);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
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
