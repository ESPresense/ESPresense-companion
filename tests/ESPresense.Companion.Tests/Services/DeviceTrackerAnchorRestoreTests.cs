using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Moq;
using MQTTnet.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Companion.Tests.Services;

public class DeviceTrackerAnchorRestoreTests
{
    private State _state;
    private string _configDir;
    private ConfigLoader _configLoader;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<MqttCoordinator> _mockMqttCoordinator;
    private Mock<DeviceSettingsStore> _mockDeviceSettingsStore;
    private TelemetryService _telemetryService;
    private DeviceTracker _deviceTracker;
    private GlobalEventDispatcher _eventDispatcher;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        var supervisorLoader = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        _mockMqttCoordinator = new Mock<MqttCoordinator>(_configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisorLoader) { CallBase = true };
        _mockMqttCoordinator.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);
        _mockDeviceSettingsStore = new Mock<DeviceSettingsStore>(_mockMqttCoordinator.Object, _state);
        _telemetryService = new TelemetryService(_mockMqttCoordinator.Object);
        _eventDispatcher = new GlobalEventDispatcher();

        _deviceTracker = new DeviceTracker(
            _state,
            _mockMqttCoordinator.Object,
            _telemetryService,
            _eventDispatcher,
            _mockDeviceSettingsStore.Object);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Test]
    public async Task CheckDeviceAsync_RestoresAnchorFromDeviceSettings()
    {
        // Arrange
        var deviceId = "test-device";
        var device = new Device(deviceId, null, TimeSpan.FromSeconds(30));
        _state.Devices[deviceId] = device;

        // Verify device is not initially anchored
        Assert.That(device.IsAnchored, Is.False, "Device should not be anchored initially");

        // Create device settings with anchor coordinates
        var deviceSettings = new DeviceSettings
        {
            Id = deviceId,
            Name = "Test Anchored Device",
            X = 5.0,
            Y = 3.0,
            Z = 1.5
        };

        // Setup the mock to return the settings
        _mockDeviceSettingsStore.Setup(m => m.Get(deviceId)).Returns(deviceSettings);
        _mockDeviceSettingsStore.Setup(m => m.Set(It.IsAny<string>(), It.IsAny<DeviceSettings>())).Returns(Task.CompletedTask);

        // Act
        // Use reflection to call the private CheckDeviceAsync method
        var checkMethod = typeof(DeviceTracker).GetMethod("CheckDeviceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<bool>)checkMethod!.Invoke(_deviceTracker, new object[] { device })!;
        var result = await task;

        // Assert
        Assert.That(result, Is.True, "CheckDeviceAsync should return true when device becomes tracked");
        Assert.That(device.IsAnchored, Is.True, "Device should be anchored after check");
        Assert.That(device.Track, Is.True, "Device should be tracked when anchored");

        // Verify anchor location
        Assert.That(device.Anchor, Is.Not.Null, "Device should have anchor object");
        Assert.That(device.Anchor!.Location.X, Is.EqualTo(5.0), "Anchor X coordinate should match settings");
        Assert.That(device.Anchor!.Location.Y, Is.EqualTo(3.0), "Anchor Y coordinate should match settings");
        Assert.That(device.Anchor!.Location.Z, Is.EqualTo(1.5), "Anchor Z coordinate should match settings");
    }

    [Test]
    public async Task CheckDeviceAsync_DoesNotRestoreAnchorIfAlreadyAnchored()
    {
        // Arrange
        var deviceId = "test-device";
        var device = new Device(deviceId, null, TimeSpan.FromSeconds(30));

        // Pre-set an anchor (simulating device already has anchor)
        var existingAnchor = new DeviceAnchor(new Point3D(1.0, 2.0, 3.0), null, null);
        device.SetAnchor(existingAnchor);

        _state.Devices[deviceId] = device;

        // Create different device settings with anchor coordinates
        var deviceSettings = new DeviceSettings
        {
            Id = deviceId,
            Name = "Test Anchored Device",
            X = 5.0,  // Different coordinates
            Y = 3.0,
            Z = 1.5
        };

        // Setup the mock to return the settings
        _mockDeviceSettingsStore.Setup(m => m.Get(deviceId)).Returns(deviceSettings);

        // Reset to the original anchor to test that it doesn't get overwritten
        device.SetAnchor(existingAnchor);

        // Act
        var checkMethod = typeof(DeviceTracker).GetMethod("CheckDeviceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<bool>)checkMethod!.Invoke(_deviceTracker, new object[] { device })!;
        var result = await task;

        // Assert
        Assert.That(device.IsAnchored, Is.True, "Device should remain anchored");
        Assert.That(device.Anchor!.Location.X, Is.EqualTo(1.0), "Original anchor X should be preserved");
        Assert.That(device.Anchor!.Location.Y, Is.EqualTo(2.0), "Original anchor Y should be preserved");
        Assert.That(device.Anchor!.Location.Z, Is.EqualTo(3.0), "Original anchor Z should be preserved");
    }

    [Test]
    public async Task CheckDeviceAsync_DoesNotRestoreAnchorWhenNoSettings()
    {
        // Arrange
        var deviceId = "test-device";
        var device = new Device(deviceId, null, TimeSpan.FromSeconds(30));
        _state.Devices[deviceId] = device;

        // No device settings exist for this device

        // Act
        var checkMethod = typeof(DeviceTracker).GetMethod("CheckDeviceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<bool>)checkMethod!.Invoke(_deviceTracker, new object[] { device })!;
        var result = await task;

        // Assert
        Assert.That(device.IsAnchored, Is.False, "Device should not be anchored when no settings exist");
    }

    [Test]
    public async Task CheckDeviceAsync_DoesNotRestoreAnchorWhenSettingsIncomplete()
    {
        // Arrange
        var deviceId = "test-device";
        var device = new Device(deviceId, null, TimeSpan.FromSeconds(30));
        _state.Devices[deviceId] = device;

        // Create device settings with incomplete anchor coordinates (missing Z)
        var deviceSettings = new DeviceSettings
        {
            Id = deviceId,
            Name = "Test Device",
            X = 5.0,
            Y = 3.0
            // Z is null
        };

        // Setup the mock to return the settings
        _mockDeviceSettingsStore.Setup(m => m.Get(deviceId)).Returns(deviceSettings);

        // Act
        var checkMethod = typeof(DeviceTracker).GetMethod("CheckDeviceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var task = (Task<bool>)checkMethod!.Invoke(_deviceTracker, new object[] { device })!;
        var result = await task;

        // Assert
        Assert.That(device.IsAnchored, Is.False, "Device should not be anchored when settings are incomplete");
    }
}
