using System.Reflection;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests;

public class DeviceTrackerTests
{
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private Mock<MqttCoordinator> _mockMqttCoordinator = null!;
    private State _state = null!;
    private TelemetryService _telemetryService = null!;
    private GlobalEventDispatcher _eventDispatcher = null!;
    private DeviceTracker _deviceTracker = null!;
    private readonly List<string> _removedDeviceIds = new();

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        var supervisorLoader = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        _mockMqttCoordinator = new Mock<MqttCoordinator>(_configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisorLoader) { CallBase = true };
        _mockMqttCoordinator.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        _state = new State(_configLoader, new NodeTelemetryStore(_mockMqttCoordinator.Object));
        _telemetryService = new TelemetryService(_mockMqttCoordinator.Object);
        var deviceSettingsStore = new DeviceSettingsStore(_mockMqttCoordinator.Object, _state);

        _eventDispatcher = new GlobalEventDispatcher();
        _removedDeviceIds.Clear();
        _eventDispatcher.DeviceRemoved += (_, e) => _removedDeviceIds.Add(e.DeviceId);

        _deviceTracker = new DeviceTracker(_state, _mockMqttCoordinator.Object, _telemetryService, _eventDispatcher, deviceSettingsStore);

        // Wire the discovery handler the same way ExecuteAsync does, without starting the background loops.
        _mockMqttCoordinator.Object.PreviousDeviceDiscovered += (_, arg) => _deviceTracker.OnPreviousDeviceDiscovered(arg);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_configDir))
            Directory.Delete(_configDir, recursive: true);
    }

    [Test]
    public void TestMultiScenarioLocator()
    {
        var configLoader = new ConfigLoader("config");
        var mqtt = new MqttCoordinator(configLoader, null, null, null);

        // Create the NodeTelemetryStore instance that's now required by State constructor
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt); // Use the existing mqtt instance
        var state = new State(configLoader, nodeTelemetryStore);
        var deviceSettingsStore = new DeviceSettingsStore(mqtt, state);

        var locator = new DeviceTracker(state, mqtt, new TelemetryService(mqtt), new GlobalEventDispatcher(), deviceSettingsStore);

        Assert.That(locator.PendingProcessCount, Is.EqualTo(0));
        Assert.That(locator.PendingLocateCount, Is.EqualTo(0));
    }

    // ---------------------------------------------------------------------
    // Coalescing locate queue
    // ---------------------------------------------------------------------

    private async Task<List<Device>> DrainLocateQueueAsync(TimeSpan quietPeriod, Action<Device>? onYield = null)
    {
        using var cts = new CancellationTokenSource(quietPeriod);
        var yielded = new List<Device>();
        try
        {
            await foreach (var device in _deviceTracker.GetConsumingEnumerable(cts.Token))
            {
                yielded.Add(device);
                onYield?.Invoke(device);
            }
        }
        catch (OperationCanceledException)
        {
            // expected: nothing more to yield within the quiet period
        }
        return yielded;
    }

    [Test]
    public async Task GetConsumingEnumerable_YieldsDeviceOnceWhenMarkedRepeatedly()
    {
        var device = new Device("coalesced", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        for (var i = 0; i < 5; i++)
            _deviceTracker.MarkForLocate(device.Id);

        Assert.That(_deviceTracker.PendingLocateCount, Is.EqualTo(1));

        var yielded = await DrainLocateQueueAsync(TimeSpan.FromMilliseconds(500));

        Assert.That(yielded, Has.Count.EqualTo(1));
        Assert.That(yielded[0], Is.SameAs(device));
        Assert.That(_deviceTracker.PendingLocateCount, Is.EqualTo(0));
    }

    [Test]
    public async Task GetConsumingEnumerable_YieldsInFirstMarkedOrder()
    {
        var a = new Device("a", null, TimeSpan.FromSeconds(30));
        var b = new Device("b", null, TimeSpan.FromSeconds(30));
        _state.Devices[a.Id] = a;
        _state.Devices[b.Id] = b;

        _deviceTracker.MarkForLocate(b.Id);
        _deviceTracker.MarkForLocate(a.Id);
        _deviceTracker.MarkForLocate(b.Id);

        var yielded = await DrainLocateQueueAsync(TimeSpan.FromMilliseconds(500));

        Assert.That(yielded.Select(d => d.Id), Is.EqualTo(new[] { "b", "a" }));
    }

    [Test]
    public async Task GetConsumingEnumerable_YieldsAgainWhenMarkedAfterDequeue()
    {
        var device = new Device("again", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;
        _deviceTracker.MarkForLocate(device.Id);

        var remarked = false;
        var yielded = await DrainLocateQueueAsync(TimeSpan.FromMilliseconds(500), d =>
        {
            // A message arriving while the device is being located must queue it for the next pass.
            if (remarked) return;
            remarked = true;
            _deviceTracker.MarkForLocate(d.Id);
        });

        Assert.That(yielded, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetConsumingEnumerable_SkipsDeviceRemovedWhilePending()
    {
        var kept = new Device("kept", null, TimeSpan.FromSeconds(30));
        _state.Devices[kept.Id] = kept;

        _deviceTracker.MarkForLocate("gone");
        _deviceTracker.MarkForLocate(kept.Id);

        var yielded = await DrainLocateQueueAsync(TimeSpan.FromMilliseconds(500));

        Assert.That(yielded, Has.Count.EqualTo(1));
        Assert.That(yielded[0], Is.SameAs(kept));
    }

    [Test]
    public async Task OnDeviceMessageReceived_CoalescesMessagesFromManyNodes()
    {
        foreach (var nodeId in new[] { "n1", "n2", "n3" })
        {
            var node = new Node(nodeId, NodeSourceType.Config);
            node.Update(new Config(), new ConfigNode { Id = nodeId, Name = nodeId, Point = new[] { 0.0, 0.0, 0.0 } }, Enumerable.Empty<Floor>());
            _state.Nodes[nodeId] = node;
        }

        for (var i = 0; i < 5; i++)
        {
            var nodeId = $"n{(i % 3) + 1}";
            await _deviceTracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs
            {
                DeviceId = "phone",
                NodeId = nodeId,
                Payload = new DeviceMessage { Distance = 1.0 + i, Rssi = -70, RefRssi = -59 }
            });
        }

        Assert.That(_state.Devices.ContainsKey("phone"), Is.True);
        Assert.That(_state.Devices["phone"].Nodes.Count, Is.EqualTo(3), "each node that heard the device is recorded");
        Assert.That(_deviceTracker.PendingProcessCount, Is.EqualTo(1), "five messages coalesce into a single pending entry");
    }

    [Test]
    public async Task OnDeviceMessageReceived_UnplacedReceiver_FeedsFingerprintingButNotRanging()
    {
        var floor = new Floor();
        floor.Update(new Config(), new ConfigFloor { Id = "first", Name = "first" });

        var placed1 = new Node("placed1", NodeSourceType.Config);
        placed1.Update(new Config(), new ConfigNode { Name = "placed1", Point = new[] { 2.0, 3.0, 0.0 } }, new[] { floor });
        _state.Nodes["placed1"] = placed1;

        var placed2 = new Node("placed2", NodeSourceType.Config);
        placed2.Update(new Config(), new ConfigNode { Name = "placed2", Point = new[] { 5.0, 0.0, 0.0 } }, new[] { floor });
        _state.Nodes["placed2"] = placed2;

        // "spare" is never configured with a point or floor, so DeviceTracker discovers it on the fly the
        // same way it would any other unknown node: Discovered source type, no location, no floor. It
        // reports the shortest distance, so an unfiltered NearestNode would pick it over either placed
        // receiver and report its (0, 0, 0) default location.
        foreach (var (nodeId, distance) in new[] { ("placed1", 2.0), ("placed2", 2.0), ("spare", 1.0) })
        {
            await _deviceTracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs
            {
                DeviceId = "phone",
                NodeId = nodeId,
                Payload = new DeviceMessage { Distance = distance, Rssi = -65, RefRssi = -59 }
            });
        }

        var device = _state.Devices["phone"];
        Assert.That(device.Nodes.Count, Is.EqualTo(3), "the unplaced receiver's reading is recorded alongside the placed ones");

        // Fingerprinting needs no node coordinates, so the unplaced receiver is a feature, not noise.
        var mapPath = Path.Combine(_configDir, "fingerprints.json");
        await File.WriteAllTextAsync(mapPath, "{\"fingerprints\": [{\"floor\": \"first\", \"x\": 1, \"y\": 0, \"z\": 0, \"rssi\": {\"placed1\": -60, \"placed2\": -70, \"spare\": -65}}]}");
        var map = new FingerprintMap(mapPath);
        var fingerprintConfig = new FingerprintConfig { Enabled = true, Mode = "knn", K = 1 };
        var fingerprintScenario = new Scenario(new Config(), new FingerprintLocator(device, floor, map, fingerprintConfig), "fingerprint");
        fingerprintScenario.Locate();
        Assert.That(fingerprintScenario.Fixes, Is.EqualTo(3), "the fingerprint locator matches against all three receivers, including the unplaced one");

        // Ranging locators must still ignore it: they only consider nodes with a known location on the floor.
        var rangingScenario = new Scenario(new Config(), new GaussNewtonMultilateralizer(device, floor, _state), "gaussnewton");
        rangingScenario.Locate();
        Assert.That(rangingScenario.Fixes, Is.EqualTo(2), "ranging locators only see the two placed receivers");

        // MultiFloor and NearestNode don't filter on Node.Floors like BaseMultilateralizer does, so they
        // need their own HasLocation check: build a placed-only device and confirm the unplaced receiver
        // changes neither locator's result.
        var placedOnlyDevice = new Device("phone-placed-only", null, TimeSpan.FromSeconds(30));
        foreach (var nodeId in new[] { "placed1", "placed2" })
            placedOnlyDevice.Nodes[nodeId] = new DeviceToNode(placedOnlyDevice, _state.Nodes[nodeId]) { Distance = 2.0, Rssi = -65, LastHit = DateTime.UtcNow };

        var multiFloorScenario = new Scenario(new Config(), new MultiFloorMultilateralizer(device, _state), "multifloor");
        multiFloorScenario.Locate();
        var multiFloorPlacedOnly = new Scenario(new Config(), new MultiFloorMultilateralizer(placedOnlyDevice, _state), "multifloor");
        multiFloorPlacedOnly.Locate();
        Assert.That(multiFloorScenario.Location, Is.EqualTo(multiFloorPlacedOnly.Location), "the unplaced receiver must not pull the multifloor solve toward the origin");

        var nearestScenario = new Scenario(new Config(), new NearestNode(device, _state), "nearestnode");
        nearestScenario.Locate();
        var nearestPlacedOnly = new Scenario(new Config(), new NearestNode(placedOnlyDevice, _state), "nearestnode");
        nearestPlacedOnly.Locate();
        Assert.That(nearestScenario.Location, Is.EqualTo(nearestPlacedOnly.Location), "the unplaced receiver must not win nearest-node and report its (0,0,0) location");
    }

    // ---------------------------------------------------------------------
    // Discovery create / delete
    // ---------------------------------------------------------------------

    private const string DiscoveryTopic = "homeassistant/device_tracker/espresense_test_device/config";

    [Test]
    public async Task ProcessDiscoveryMessage_WithPayloadCreatesTrackedDevice()
    {
        PreviousDeviceDiscoveredEventArgs? received = null;
        _mockMqttCoordinator.Object.PreviousDeviceDiscovered += (_, arg) => received = arg;

        var payload = JsonConvert.SerializeObject(new
        {
            name = "Test Device",
            unique_id = "espresense-companion-test-device",
            state_topic = "espresense/companion/test-device",
            origin = new { name = "ESPresense Companion" }
        });

        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(DiscoveryTopic, payload);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Topic, Is.EqualTo(DiscoveryTopic));
        Assert.That(received.DiscoveryId, Is.EqualTo("espresense_test_device"));
        Assert.That(received.DeviceId, Is.EqualTo("test-device"));
        Assert.That(received.AutoDiscover, Is.Not.Null);

        Assert.That(_state.Devices.TryGetValue("test-device", out var device), Is.True);
        Assert.That(device!.Track, Is.True);
        Assert.That(device.Name, Is.EqualTo("Test Device"));
        Assert.That(device.HassAutoDiscovery.Single().DiscoveryId, Is.EqualTo("espresense_test_device"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public async Task ProcessDiscoveryMessage_EmptyPayloadRemovesDevice(string? payload)
    {
        PreviousDeviceDiscoveredEventArgs? received = null;
        _mockMqttCoordinator.Object.PreviousDeviceDiscovered += (_, arg) => received = arg;

        // Device created earlier from a retained discovery message carrying this discovery id
        var device = new Device("test-device", "espresense_test_device", TimeSpan.FromSeconds(30)) { Track = true };
        _state.Devices[device.Id] = device;

        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(DiscoveryTopic, payload);

        Assert.That(received, Is.Not.Null, "an empty payload is a retained-message clear, not a malformed message");
        Assert.That(received!.AutoDiscover, Is.Null);
        Assert.That(received.DeviceId, Is.Null);
        Assert.That(received.DiscoveryId, Is.EqualTo("espresense_test_device"));
        Assert.That(_state.Devices.ContainsKey("test-device"), Is.False, "device removed from State.Devices");
        Assert.That(_removedDeviceIds, Is.EqualTo(new[] { "test-device" }), "DeviceRemoved raised so clients drop the device");
    }

    [Test]
    public async Task ProcessDiscoveryMessage_EmptyPayloadRemovesDeviceCreatedFromMessages()
    {
        // Devices created from node messages get the default discovery id, which is what the companion publishes
        // to the discovery topic, so a clear of that topic must find them too.
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30)) { Track = true };
        _state.Devices[device.Id] = device;
        var topic = $"homeassistant/device_tracker/{device.HassAutoDiscovery.Single().DiscoveryId}/config";

        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(topic, "");

        Assert.That(_state.Devices.ContainsKey("test-device"), Is.False);
    }

    [Test]
    public async Task ProcessDiscoveryMessage_EmptyPayloadForUnknownDeviceIsIgnored()
    {
        var other = new Device("other", null, TimeSpan.FromSeconds(30));
        _state.Devices[other.Id] = other;

        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage("homeassistant/device_tracker/espresense_nobody/config", "");

        Assert.That(_state.Devices.Count, Is.EqualTo(1));
        Assert.That(_state.Devices.ContainsKey("other"), Is.True);
    }

    private static Task<bool> CheckDeviceAsync(DeviceTracker tracker, Device device)
    {
        var checkMethod = typeof(DeviceTracker).GetMethod("CheckDeviceAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        return (Task<bool>)checkMethod!.Invoke(tracker, new object[] { device })!;
    }

    [Test]
    public async Task UntrackClearEcho_DoesNotRemoveDevice_ButExternalClearDoes()
    {
        // Tracked device with no config match: the next check untracks it, which publishes a retained clear
        // of its discovery config. That clear echoes back through our own subscription.
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30)) { Track = true, Check = true };
        _state.Devices[device.Id] = device;
        var discoveryId = device.HassAutoDiscovery.Single().DiscoveryId;
        var topic = $"homeassistant/device_tracker/{discoveryId}/config";

        var trackChanged = await CheckDeviceAsync(_deviceTracker, device);
        Assert.That(trackChanged, Is.True);
        Assert.That(device.Track, Is.False);
        _mockMqttCoordinator.Verify(m => m.EnqueueAsync(topic, null, true), Times.Once, "untrack publishes the discovery clear");

        // (a) the echo of our own clear must not evict the device
        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(topic, "");
        Assert.That(_state.Devices.ContainsKey("test-device"), Is.True, "self-published clear keeps the device");
        Assert.That(_removedDeviceIds, Is.Empty);

        // (b) a later clear with no registry entry is external and removes it
        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(topic, "");
        Assert.That(_state.Devices.ContainsKey("test-device"), Is.False, "external clear removes the device");
        Assert.That(_removedDeviceIds, Is.EqualTo(new[] { "test-device" }));
    }

    [Test]
    public async Task ExternalClear_WithoutRegistryEntry_RemovesDevice()
    {
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30)) { Track = true };
        _state.Devices[device.Id] = device;
        var topic = $"homeassistant/device_tracker/{device.HassAutoDiscovery.Single().DiscoveryId}/config";

        await _mockMqttCoordinator.Object.ProcessDiscoveryMessage(topic, "");

        Assert.That(_state.Devices.ContainsKey("test-device"), Is.False);
        Assert.That(_removedDeviceIds, Is.EqualTo(new[] { "test-device" }));
    }

    [Test]
    public void MarkIdleDevices_QueuesTrackedDeviceThatWasNeverLocated()
    {
        // Message-created devices never set LastCalculated; they must still be picked up by the idle check.
        var never = new Device("never-located", null, TimeSpan.FromSeconds(30)) { Track = true };
        var recent = new Device("recent", null, TimeSpan.FromSeconds(30)) { Track = true, LastCalculated = DateTime.UtcNow };
        var stale = new Device("stale", null, TimeSpan.FromSeconds(30)) { Track = true, LastCalculated = DateTime.UtcNow.AddMinutes(-5) };
        var untracked = new Device("untracked", null, TimeSpan.FromSeconds(30)) { Track = false };
        foreach (var d in new[] { never, recent, stale, untracked }) _state.Devices[d.Id] = d;

        _deviceTracker.MarkIdleDevices(DateTime.UtcNow);

        Assert.That(_deviceTracker.PendingProcessCount, Is.EqualTo(2), "never-located and stale devices are queued; recent and untracked are not");
    }

    [Test]
    public void OnPreviousDeviceDiscovered_IgnoresEchoOfOurOwnClear()
    {
        var device = new Device("echo", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;
        _deviceTracker.RememberSelfClear("disc-echo");

        _deviceTracker.OnPreviousDeviceDiscovered(DiscoveryClear("disc-echo", device.Id));

        Assert.That(_state.Devices.ContainsKey(device.Id), Is.True);
    }

    [Test]
    public void OnPreviousDeviceDiscovered_StaleSelfClearDoesNotHideExternalDelete()
    {
        // Read-only mode publishes nothing, so no echo ever consumes the entry; a later external clear must still delete.
        var device = new Device("stale-echo", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;
        _deviceTracker.RememberSelfClear("disc-stale", DateTime.UtcNow.AddMinutes(-6));

        _deviceTracker.OnPreviousDeviceDiscovered(DiscoveryClear("disc-stale", device.Id));

        Assert.That(_state.Devices.ContainsKey(device.Id), Is.False);
    }

    private static PreviousDeviceDiscoveredEventArgs DiscoveryClear(string discoveryId, string deviceId) => new()
    {
        Topic = $"homeassistant/device_tracker/{discoveryId}/config",
        DiscoveryId = discoveryId,
        DeviceId = deviceId,
        AutoDiscover = null
    };

    [Test]
    public void OnPreviousDeviceDiscovered_DeleteUsesDeviceIdWhenProvided()
    {
        var device = new Device("by-id", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        _deviceTracker.OnPreviousDeviceDiscovered(new PreviousDeviceDiscoveredEventArgs
        {
            Topic = "homeassistant/device_tracker/whatever/config",
            DiscoveryId = "whatever",
            DeviceId = "by-id",
            AutoDiscover = null
        });

        Assert.That(_state.Devices.ContainsKey("by-id"), Is.False);
    }
}
