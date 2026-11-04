using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Controllers;

[TestFixture]
public class StateControllerRoomsTests
{
    private Config _config = new();
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;
    private CsiMotionTracker _tracker = null!;
    private CsiFusionService _fusion = null!;
    private StateController _controller = null!;

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
        _fusion = new CsiFusionService(_state, _tracker, mqtt.Object, new GlobalEventDispatcher(), Mock.Of<ILogger<CsiFusionService>>());

        var nodeSettingsStore = new Mock<NodeSettingsStore>(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        var deviceSettingsStore = new Mock<DeviceSettingsStore>(mqtt.Object, _state);
        _controller = new StateController(
            Mock.Of<ILogger<StateController>>(),
            _state,
            _configLoader,
            nodeSettingsStore.Object,
            deviceSettingsStore.Object,
            nodeTelemetryStore,
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()),
            new GlobalEventDispatcher(),
            new CalibrationService(_state, nodeSettingsStore.Object),
            new DeviceEnricher(deviceSettingsStore.Object),
            _fusion);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) try { Directory.Delete(_configDir, true); } catch { /* ignore */ }
    }

    [Test]
    public void GetRooms_ReturnsCurrentFusionOccupancy_ForCsiMappedRoomsOnly()
    {
        var floor = new Floor();
        floor.Update(_config, new ConfigFloor
        {
            Id = "first",
            Name = "first",
            Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 10.0, 10, 3 } },
            Rooms = new[] { new ConfigRoom { Name = "Office", Points = new[] { new[] { 0.0, 0 }, new[] { 4.0, 0 }, new[] { 4.0, 4 }, new[] { 0.0, 4 } } } }
        });
        _state.Floors["first"] = floor;

        var node = new Node("office-node", NodeSourceType.Config);
        node.Update(_config, new ConfigNode { Id = "office-node", Name = "office-node", Point = new[] { 1.0, 1, 1 } }, new[] { floor });
        _state.Nodes["office-node"] = node;

        var now = DateTime.UtcNow;
        for (var i = 0; i < 20; i++)
            _tracker.Observe("office-node", 10, now.AddSeconds(-200 + i * 5));
        _tracker.Observe("office-node", 100, now.AddSeconds(1));
        _tracker.Observe("office-node", 100, now.AddSeconds(2));

        _fusion.Tick(now.AddSeconds(2));

        var rooms = _controller.GetRooms().ToList();

        Assert.That(rooms, Has.Count.EqualTo(1));
        Assert.That(rooms[0].RoomId, Is.EqualTo("office"));
        Assert.That(rooms[0].Occupied, Is.True);
    }
}
