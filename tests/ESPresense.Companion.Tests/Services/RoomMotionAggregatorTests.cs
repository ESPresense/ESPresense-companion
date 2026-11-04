using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

[TestFixture]
public class RoomMotionAggregatorTests
{
    private Config _config = new();
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private CsiMotionTracker _tracker = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), "floors: []\n");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.LoadAsync();

        var mqtt = new Mock<IMqttCoordinator>();
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        _state = new State(_configLoader, nodeTelemetryStore);
        _tracker = new CsiMotionTracker(mqtt.Object);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) try { Directory.Delete(_configDir, true); } catch { /* ignore */ }
    }

    private Floor BuildFloor(string id, params ConfigRoom[] rooms)
    {
        var floor = new Floor();
        floor.Update(_config, new ConfigFloor { Id = id, Name = id, Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } }, Rooms = rooms });
        return floor;
    }

    private static ConfigRoom Square(string name, double x0, double y0, double x1, double y1) => new()
    {
        Name = name,
        Points = new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }
    };

    private Node AddNode(string id, Point3D location, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_config, new ConfigNode { Id = id, Name = id, Point = new[] { location.X, location.Y, location.Z } }, new[] { floor });
        _state.Nodes[id] = node;
        return node;
    }

    [Test]
    public void Compute_MultipleNodesInOneRoom_TakesTheMax()
    {
        var laundry = BuildFloor("first", Square("Laundry", 0, 0, 4, 4));
        AddNode("laundry-a", new Point3D(1, 1, 1), laundry);
        AddNode("laundry-b", new Point3D(2, 2, 1), laundry);

        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        // Warm both nodes to a baseline of ~10.
        for (var i = 0; i < 20; i++)
        {
            _tracker.Observe("laundry-a", 10, start.AddSeconds(i * 5));
            _tracker.Observe("laundry-b", 10, start.AddSeconds(i * 5));
        }

        // Only node b sees an elevated (but not yet "motion") reading; its score should win the max.
        _tracker.Observe("laundry-a", 10, start.AddSeconds(100));
        _tracker.Observe("laundry-b", 50, start.AddSeconds(100));

        var byRoom = RoomMotionAggregator.Compute(_state, _tracker, start.AddSeconds(100));

        Assert.That(byRoom.ContainsKey("laundry"), Is.True);
        Assert.That(byRoom["laundry"].MaxScore, Is.EqualTo(5.0).Within(0.01));
        Assert.That(byRoom["laundry"].NodeIds, Is.EquivalentTo(new[] { "laundry-a", "laundry-b" }));
    }

    [Test]
    public void Compute_NodeOutsideEveryRoom_ContributesNothingAndDoesNotCrash()
    {
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4));
        AddNode("hallway", new Point3D(8, 8, 1), floor);

        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        _tracker.Observe("hallway", 10, start);

        Dictionary<string, RoomMotion>? byRoom = null;
        Assert.DoesNotThrow(() => byRoom = RoomMotionAggregator.Compute(_state, _tracker, start));

        Assert.That(byRoom, Is.Not.Null);
        Assert.That(byRoom!.Count, Is.EqualTo(0));
    }

    [Test]
    public void Compute_RoomWithMotionOnAnyNode_IsFlaggedAsMotion()
    {
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4));
        AddNode("laundry-a", new Point3D(1, 1, 1), floor);

        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 20; i++)
            _tracker.Observe("laundry-a", 10, start.AddSeconds(i * 5));

        _tracker.Observe("laundry-a", 100, start.AddSeconds(100));
        _tracker.Observe("laundry-a", 100, start.AddSeconds(105));

        var byRoom = RoomMotionAggregator.Compute(_state, _tracker, start.AddSeconds(105));

        Assert.That(byRoom["laundry"].Motion, Is.True);
    }
}
