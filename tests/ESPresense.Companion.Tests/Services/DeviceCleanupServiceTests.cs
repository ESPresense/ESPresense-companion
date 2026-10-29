using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Services;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeviceCleanupServiceTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private DeviceSettingsStore _store = null!;
    private DeviceCleanupService _cleanup = null!;
    private Func<DeviceSettingsEventArgs, Task> _receive = null!;
    private string _configDir = null!;

    [SetUp]
    public async Task Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.SetupGet(m => m.DiscoveryTopic).Returns("homeassistant");
        _mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        var subscribed = new TaskCompletionSource<Func<DeviceSettingsEventArgs, Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mqtt.SetupAdd(m => m.DeviceConfigReceivedAsync += It.IsAny<Func<DeviceSettingsEventArgs, Task>>())
            .Callback<Func<DeviceSettingsEventArgs, Task>>(h => subscribed.TrySetResult(h));

        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(_mqtt.Object));
        _store = new DeviceSettingsStore(_mqtt.Object, _state);
        var deviceService = new DeviceService(_state, _mqtt.Object, new GlobalEventDispatcher(), NullLogger<DeviceService>.Instance);
        _cleanup = new DeviceCleanupService(_state, deviceService, _store, NullLogger<DeviceCleanupService>.Instance);

        await _store.StartAsync(CancellationToken.None);
        _receive = await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _cleanup.StartAsync(CancellationToken.None); // subscribes before its first delay
    }

    [TearDown]
    public async Task TearDown()
    {
        await _cleanup.StopAsync(CancellationToken.None);
        _cleanup.Dispose();
        await _store.StopAsync(CancellationToken.None);
        _store.Dispose();
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch (IOException) { }
    }

    private Device AddDevice(string id)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30));
        _state.Devices[id] = device;
        return device;
    }

    private Task Receive(string key, DeviceSettings ds) => _receive(new DeviceSettingsEventArgs { DeviceId = key, Payload = ds });

    private void VerifyCleared(Device device)
    {
        var id = device.Id;
        var discoveryId = device.HassAutoDiscovery.Single().DiscoveryId;
        _mqtt.Verify(m => m.EnqueueAsync($"homeassistant/device_tracker/{discoveryId}/config", null, true), Times.Once);
        _mqtt.Verify(m => m.EnqueueAsync($"espresense/companion/{id}", null, true), Times.Once);
        _mqtt.Verify(m => m.EnqueueAsync($"espresense/companion/{id}/attributes", null, true), Times.Once);
    }

    [Test]
    public async Task AliasRename_RemovesOldAliasAndRawKeyEntities()
    {
        var raw = AddDevice("irk:70f7");
        await Receive("irk:70f7", new DeviceSettings { Id = "phone:piper" });

        Assert.That(_state.Devices.ContainsKey("irk:70f7"), Is.False);
        VerifyCleared(raw);

        var piper = AddDevice("phone:piper");
        await Receive("irk:70f7", new DeviceSettings { Id = "phone:old-iphone" });

        Assert.That(_state.Devices.ContainsKey("phone:piper"), Is.False);
        VerifyCleared(piper);
        Assert.That(_state.Devices.ContainsKey("phone:old-iphone"), Is.False, "never existed; nothing to do");
    }

    [Test]
    public async Task AliasTakenOverByAnotherDevice_IsLeftAlone()
    {
        await Receive("keys:one", new DeviceSettings { Id = "keys:shared" });
        await Receive("keys:two", new DeviceSettings { Id = "keys:shared" });
        AddDevice("keys:shared");

        await Receive("keys:one", new DeviceSettings { Id = "keys:one-new" });

        Assert.That(_state.Devices.ContainsKey("keys:shared"), Is.True);
    }

    [Test]
    public async Task CleanupPass_RemovesRawKeyRebuiltFromDiscoveryAfterRestart()
    {
        // After a restart the retained settings arrive before the retained discovery that rebuilds the raw-key device
        await Receive("irk:70f7", new DeviceSettings { Id = "phone:piper" });
        var raw = AddDevice("irk:70f7");
        AddDevice("phone:piper");
        AddDevice("tile:unrelated");

        await _cleanup.RunCleanup(CancellationToken.None);

        Assert.That(_state.Devices.Keys, Is.EquivalentTo(new[] { "phone:piper", "tile:unrelated" }));
        VerifyCleared(raw);
    }
}
