using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Services.Occupancy;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Services.Occupancy;

// CMP-2 (ESPA-198): RoomOccupancyPublisher wires CMP-1's OccupancyFusion signal into the same
// MQTT/HA autodiscovery hooks device_tracker already uses, so HA users can build HVAC
// automations against per-room occupancy.
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class RoomOccupancyPublisherTests
{
    private Mock<IMqttCoordinator> _mockMqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private GlobalEventDispatcher _dispatcher = null!;
    private RoomOccupancyPublisher _publisher = null!;
    private Dictionary<string, string?> _published = null!;
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        _published = new Dictionary<string, string?>();
        _mockMqtt = new Mock<IMqttCoordinator>();
        _mockMqtt.Setup(m => m.DiscoveryTopic).Returns("homeassistant");
        _mockMqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((topic, payload, _) => _published[topic] = payload)
            .Returns(Task.CompletedTask);
        _mockMqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string?, bool>((topic, payload, _) => _published[topic] = payload)
            .ReturnsAsync(true);

        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        var nodeTelemetryStore = new NodeTelemetryStore(_mockMqtt.Object);
        _state = new State(_configLoader, nodeTelemetryStore);
        _dispatcher = new GlobalEventDispatcher();
        _publisher = new RoomOccupancyPublisher(_state, _mockMqtt.Object, _dispatcher);

        var floor = _state.Floors.GetOrAdd("ground", _ => new Floor());
        floor.Update(new Config(), new ConfigFloor { Id = "ground", Rooms = new[] { new ConfigRoom { Id = "kitchen", Name = "Kitchen" } } });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
    }

    private Room Kitchen => _state.Floors["ground"].Rooms["kitchen"];

    private Device AddTrackedDevice(string id, Room? room)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30)) { Track = true };
        if (room != null)
            device.SetAnchor(new DeviceAnchor(new Point3D(0, 0, 0), _state.Floors["ground"], room));
        _state.Devices[id] = device;
        return device;
    }

    [Test]
    public void ResolveBlePresence_NoTrackedDevices_ReturnsNull()
    {
        var result = RoomOccupancyPublisher.ResolveBlePresence("kitchen", Array.Empty<Device>());
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ResolveBlePresence_TrackedDevicesElsewhere_ReturnsFalse()
    {
        var elsewhere = new Device("phone1", null, TimeSpan.FromSeconds(30)) { Track = true };
        var result = RoomOccupancyPublisher.ResolveBlePresence("kitchen", new[] { elsewhere });
        Assert.That(result, Is.False);
    }

    [Test]
    public void ResolveBlePresence_TrackedDeviceInRoom_ReturnsTrue()
    {
        var device = AddTrackedDevice("phone1", Kitchen);
        var result = RoomOccupancyPublisher.ResolveBlePresence("kitchen", new[] { device });
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task PublishAllRoomsAsync_DeviceInRoom_PublishesOccupiedAndDiscoveryConfig()
    {
        AddTrackedDevice("phone1", Kitchen);

        await _publisher.PublishAllRoomsAsync(CancellationToken.None);

        Assert.That(_published["espresense/companion/rooms/kitchen/occupancy"], Is.EqualTo("ON"));
        Assert.That(_published.Keys.Any(k => k.StartsWith("homeassistant/binary_sensor/") && k.Contains("kitchen")), Is.True);
    }

    [Test]
    public async Task PublishAllRoomsAsync_NoTrackedDevices_PublishesVacant()
    {
        await _publisher.PublishAllRoomsAsync(CancellationToken.None);

        Assert.That(_published["espresense/companion/rooms/kitchen/occupancy"], Is.EqualTo("OFF"));
    }

    [Test]
    public async Task PublishAllRoomsAsync_UnchangedOccupancy_DoesNotRepublishState()
    {
        AddTrackedDevice("phone1", Kitchen);
        await _publisher.PublishAllRoomsAsync(CancellationToken.None);
        _published.Clear();

        await _publisher.PublishAllRoomsAsync(CancellationToken.None);

        Assert.That(_published.ContainsKey("espresense/companion/rooms/kitchen/occupancy"), Is.False);
    }

    [Test]
    public async Task PublishAllRoomsAsync_OccupancyChanges_RepublishesState()
    {
        var device = AddTrackedDevice("phone1", Kitchen);
        await _publisher.PublishAllRoomsAsync(CancellationToken.None);

        device.SetAnchor(null);
        await _publisher.PublishAllRoomsAsync(CancellationToken.None);

        Assert.That(_published["espresense/companion/rooms/kitchen/occupancy"], Is.EqualTo("OFF"));
    }
}
