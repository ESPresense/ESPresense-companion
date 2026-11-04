using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

[TestFixture]
public class CsiFusionServiceTests
{
    private Config _config = new();
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private CsiMotionTracker _tracker = null!;
    private Mock<IMqttCoordinator> _mqtt = null!;
    private CsiFusionService _fusion = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), "floors: []\n");
        _configLoader = new ConfigLoader(_configDir);
        await _configLoader.LoadAsync();

        _mqtt = new Mock<IMqttCoordinator>();
        var nodeTelemetryStore = new NodeTelemetryStore(_mqtt.Object);
        _state = new State(_configLoader, nodeTelemetryStore);
        _tracker = new CsiMotionTracker(_mqtt.Object);
        _fusion = new CsiFusionService(_state, _tracker, _mqtt.Object, new GlobalEventDispatcher(), Mock.Of<ILogger<CsiFusionService>>());
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
        _state.Floors[floor.Id!] = floor;
        return floor;
    }

    private static ConfigRoom Square(string name, double x0, double y0, double x1, double y1) => new()
    {
        Name = name,
        Points = new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } }
    };

    private Node AddCsiNode(string id, Point3D location, Floor floor)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_config, new ConfigNode { Id = id, Name = id, Point = new[] { location.X, location.Y, location.Z } }, new[] { floor });
        _state.Nodes[id] = node;
        return node;
    }

    private static Scenario MakeScenario(Room? room, double probability)
    {
        var s = new Scenario(null, null!, "test")
        {
            Room = room,
            Probability = probability,
            LastHit = DateTime.UtcNow
        };
        return s;
    }

    private Device AddDevice(string id, Room? bestRoom, params Scenario[] scenarios)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30)) { Track = true };
        foreach (var s in scenarios) device.Scenarios.Add(s);
        device.BestScenario = scenarios.FirstOrDefault(s => s.Room == bestRoom) ?? (bestRoom == null ? null : MakeScenario(bestRoom, 1.0));
        if (device.BestScenario != null && !device.Scenarios.Contains(device.BestScenario)) device.Scenarios.Add(device.BestScenario);
        _state.Devices[id] = device;
        return device;
    }

    private Room RoomById(string floorId, string roomId) => _state.Floors[floorId].Rooms[roomId];

    [Test]
    public void NoCsiDataAtAll_DeviceRoomIsByteForByteIdenticalToBleOnlyResolution()
    {
        // Neither room this device flaps between has any CSI-mapped node.
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4), Square("Family", 5, 5, 9, 9));
        var laundry = RoomById("first", "laundry");
        var family = RoomById("first", "family");

        var device = AddDevice("dev-1", laundry, MakeScenario(laundry, 0.55), MakeScenario(family, 0.45));

        for (var i = 0; i < 5; i++)
        {
            device.BestScenario = i % 2 == 0 ? device.Scenarios.First(s => s.Room == laundry) : device.Scenarios.First(s => s.Room == family);
            _fusion.Tick(DateTime.UtcNow);
            Assert.That(device.FusedRoom, Is.Null, "with no CSI coverage at all, FusedRoom must never override BestScenario");
            Assert.That(device.Room, Is.SameAs(device.BestScenario!.Room));
        }
    }

    [Test]
    public void TieBreak_ResolvesCloseFlapTowardTheRoomShowingMotion()
    {
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4), Square("Family", 5, 5, 9, 9));
        var laundry = RoomById("first", "laundry");
        var family = RoomById("first", "family");

        AddCsiNode("laundry-node", new Point3D(1, 1, 1), floor);
        AddCsiNode("family-node", new Point3D(6, 6, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            _tracker.Observe("laundry-node", 10, now.AddSeconds(-200 + i * 5));
            _tracker.Observe("family-node", 10, now.AddSeconds(-200 + i * 5));
        }
        // Laundry shows sustained motion, Family does not.
        _tracker.Observe("laundry-node", 100, now.AddSeconds(-10));
        _tracker.Observe("laundry-node", 100, now.AddSeconds(-5));

        var laundryScenario = MakeScenario(laundry, 0.52);
        var familyScenario = MakeScenario(family, 0.48);
        var device = AddDevice("dev-1", family, laundryScenario, familyScenario);
        device.BestScenario = familyScenario; // BLE currently leans (barely) toward the quiet room

        _fusion.Tick(now);

        Assert.That(device.Room?.Id, Is.EqualTo("laundry"), "a close tie must resolve toward the room with motion");
    }

    [Test]
    public void Damping_HoldsOldRoom_WhenBleMovesToARoomWithNoMotion_ThenCommitsAfterTheWindow()
    {
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4), Square("Family", 5, 5, 9, 9));
        var laundry = RoomById("first", "laundry");
        var family = RoomById("first", "family");

        AddCsiNode("laundry-node", new Point3D(1, 1, 1), floor);
        AddCsiNode("family-node", new Point3D(6, 6, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            _tracker.Observe("laundry-node", 10, now.AddSeconds(-200 + i * 5));
            _tracker.Observe("family-node", 10, now.AddSeconds(-200 + i * 5));
        }

        var laundryScenario = MakeScenario(laundry, 0.9);
        var familyScenario = MakeScenario(family, 0.05); // not a close tie, so the tie-break never applies
        var device = AddDevice("dev-1", laundry, laundryScenario, familyScenario);

        _fusion.Tick(now);
        Assert.That(device.Room?.Id, Is.EqualTo("laundry"));

        // BLE now says Family, which shows no motion -- should be damped, not committed immediately.
        device.BestScenario = familyScenario;
        _fusion.Tick(now.AddSeconds(2));
        Assert.That(device.Room?.Id, Is.EqualTo("laundry"), "a move into a motionless room must be damped, not committed immediately");

        // Still within the damping window.
        _fusion.Tick(now.AddSeconds(8));
        Assert.That(device.Room?.Id, Is.EqualTo("laundry"), "must still be held before the damping window elapses");

        // Past the damping window: a real, sustained move commits.
        _fusion.Tick(now.AddSeconds(13));
        Assert.That(device.Room?.Id, Is.EqualTo("family"), "a move held past the damping window must eventually commit");
    }

    [Test]
    public void Damping_CommitsImmediately_WhenTargetRoomHasMotion()
    {
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4), Square("Family", 5, 5, 9, 9));
        var laundry = RoomById("first", "laundry");
        var family = RoomById("first", "family");

        AddCsiNode("laundry-node", new Point3D(1, 1, 1), floor);
        AddCsiNode("family-node", new Point3D(6, 6, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
        {
            _tracker.Observe("laundry-node", 10, now.AddSeconds(-200 + i * 5));
            _tracker.Observe("family-node", 10, now.AddSeconds(-200 + i * 5));
        }

        var laundryScenario = MakeScenario(laundry, 0.9);
        var familyScenario = MakeScenario(family, 0.05);
        var device = AddDevice("dev-1", laundry, laundryScenario, familyScenario);
        _fusion.Tick(now);

        // Family now genuinely shows motion -- the move should commit on the very next tick.
        _tracker.Observe("family-node", 100, now.AddSeconds(1));
        _tracker.Observe("family-node", 100, now.AddSeconds(2));
        device.BestScenario = familyScenario;
        _fusion.Tick(now.AddSeconds(2));

        Assert.That(device.Room?.Id, Is.EqualTo("family"), "a move into a room that already shows motion must not be damped");
    }

    [Test]
    public void Attribution_CreditsTheDeviceHeadingIntoTheRoomWhenMotionStarts()
    {
        var floor = BuildFloor("first", Square("Office", 0, 0, 4, 4));
        var office = RoomById("first", "office");
        AddCsiNode("office-node", new Point3D(1, 1, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.Observe("office-node", 10, now.AddSeconds(-200 + i * 5));

        var officeScenario = MakeScenario(office, 0.9);
        var device = AddDevice("dev-1", office, officeScenario);

        // No motion yet.
        var changedBefore = _fusion.Tick(now);
        Assert.That(changedBefore.Any(o => o.RoomId == "office" && o.Occupied), Is.False);

        // Motion starts while the device is already resolved into the room.
        _tracker.Observe("office-node", 100, now.AddSeconds(1));
        _tracker.Observe("office-node", 100, now.AddSeconds(2));
        var changed = _fusion.Tick(now.AddSeconds(2));

        var occupancy = changed.Single(o => o.RoomId == "office");
        Assert.That(occupancy.Occupied, Is.True);
        Assert.That(occupancy.Who, Does.Contain("dev-1"));
        Assert.That(occupancy.Unknown, Is.False);
    }

    [Test]
    public void UnknownOccupant_FlaggedWhenMotionStartsWithNoPlausibleDevice()
    {
        var floor = BuildFloor("first", Square("Office", 0, 0, 4, 4));
        AddCsiNode("office-node", new Point3D(1, 1, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.Observe("office-node", 10, now.AddSeconds(-200 + i * 5));

        // No tracked device at all.
        _tracker.Observe("office-node", 100, now.AddSeconds(1));
        _tracker.Observe("office-node", 100, now.AddSeconds(2));
        var changed = _fusion.Tick(now.AddSeconds(2));

        var occupancy = changed.Single(o => o.RoomId == "office");
        Assert.That(occupancy.Occupied, Is.True);
        Assert.That(occupancy.Who, Is.Empty);
        Assert.That(occupancy.Unknown, Is.True);
    }

    [Test]
    public void ReferenceFixture_RoomOscillatingEvery5To30Seconds_ResolvesToTheRoomWithSustainedMotion()
    {
        // Reproduces the documented real-world case: a device's BLE room bounces between two adjacent
        // rooms every 5-30s while one room (Laundry) shows sustained CSI motion and the other (Family)
        // never does.
        var floor = BuildFloor("first", Square("Laundry", 0, 0, 4, 4), Square("Family", 5, 5, 9, 9));
        var laundry = RoomById("first", "laundry");
        var family = RoomById("first", "family");

        AddCsiNode("laundry-node", new Point3D(1, 1, 1), floor);
        AddCsiNode("family-node", new Point3D(6, 6, 1), floor);

        var start = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 20; i++)
        {
            _tracker.Observe("laundry-node", 10, start.AddSeconds(i * 5));
            _tracker.Observe("family-node", 10, start.AddSeconds(i * 5));
        }
        var baselineEnd = start.AddSeconds(20 * 5);

        // Laundry goes, and stays, elevated for the whole flapping episode that follows.
        _tracker.Observe("laundry-node", 90, baselineEnd.AddSeconds(5));
        _tracker.Observe("laundry-node", 90, baselineEnd.AddSeconds(10));

        var laundryScenario = MakeScenario(laundry, 0.5);
        var familyScenario = MakeScenario(family, 0.5);
        var device = AddDevice("dev-1", laundry, laundryScenario, familyScenario);

        // 5-30s flap pattern between the two rooms, each with near-tied probability (ponytail: real
        // scenario probabilities hover close during a flap rather than snapping to exactly 0.5/0.5).
        var flapOffsets = new[] { 15, 20, 35, 55, 65, 90, 95, 120 };
        var toLaundry = true;
        foreach (var offsetSeconds in flapOffsets)
        {
            var at = baselineEnd.AddSeconds(offsetSeconds);
            laundryScenario.Probability = toLaundry ? 0.52 : 0.48;
            familyScenario.Probability = toLaundry ? 0.48 : 0.52;
            device.BestScenario = toLaundry ? laundryScenario : familyScenario;
            // Keep laundry's motion sustained across the whole flapping window.
            _tracker.Observe("laundry-node", 90, at);
            _tracker.Observe("family-node", 10, at);

            _fusion.Tick(at);

            Assert.That(device.Room?.Id, Is.EqualTo("laundry"),
                $"flap at +{offsetSeconds}s toward {(toLaundry ? "laundry" : "family")} must still resolve to laundry");

            toLaundry = !toLaundry;
        }
    }

    [Test]
    public void Tick_DoesNotReportTheSameUnchangedOccupancyEveryTick()
    {
        var floor = BuildFloor("first", Square("Office", 0, 0, 4, 4));
        AddCsiNode("office-node", new Point3D(1, 1, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.Observe("office-node", 10, now.AddSeconds(-200 + i * 5));

        _tracker.Observe("office-node", 100, now.AddSeconds(1));
        _tracker.Observe("office-node", 100, now.AddSeconds(2));
        var first = _fusion.Tick(now.AddSeconds(2));
        Assert.That(first, Is.Not.Empty);

        // Same motion state, same (empty) who-list -- nothing actually changed.
        _tracker.Observe("office-node", 100, now.AddSeconds(7));
        var second = _fusion.Tick(now.AddSeconds(7));
        Assert.That(second, Is.Empty, "an unchanged occupancy snapshot must not be reported as changed");
    }

    [Test]
    public void Tick_WhenDisabledAtRuntime_ClearsFusedRoomAndPublishesClearedOccupancy()
    {
        // A config reload updates this already-running CsiFusionService in place rather than
        // restarting it, so disabling csi_fusion.enabled at runtime must actively clear state.
        var floor = BuildFloor("first", Square("Office", 0, 0, 4, 4));
        var office = RoomById("first", "office");
        AddCsiNode("office-node", new Point3D(1, 1, 1), floor);

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.Observe("office-node", 10, now.AddSeconds(-200 + i * 5));

        var officeScenario = MakeScenario(office, 0.9);
        var device = AddDevice("dev-1", office, officeScenario);

        _tracker.Observe("office-node", 100, now.AddSeconds(1));
        _tracker.Observe("office-node", 100, now.AddSeconds(2));
        var occupied = _fusion.Tick(now.AddSeconds(2));
        Assert.That(occupied.Single(o => o.RoomId == "office").Occupied, Is.True);
        Assert.That(_fusion.CurrentOccupancy["office"].Occupied, Is.True);

        _state.Config!.CsiFusion.Enabled = false;
        var cleared = _fusion.Tick(now.AddSeconds(4));

        Assert.That(cleared.Single().RoomId, Is.EqualTo("office"));
        Assert.That(cleared.Single().Occupied, Is.False, "disabling fusion must publish a cleared (OFF) snapshot, not just stop updating");
        Assert.That(_fusion.CurrentOccupancy, Is.Empty, "disabled fusion must not keep serving stale occupancy from CurrentOccupancy/GET /api/state/rooms");
        Assert.That(device.FusedRoom, Is.Null, "disabling fusion must release any tie-break/damping override back to BestScenario");
    }
}
