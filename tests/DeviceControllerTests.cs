using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests;

public class DeviceControllerTests
{
    private Mock<ILogger<DeviceController>> _mockLogger;
    private Mock<DeviceSettingsStore> _mockDeviceSettingsStore;
    private Mock<State> _mockState;
    private DeviceController _deviceController;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<DeviceController>>();
        
        // Create mocks with interface
        var mockConfigLoader = new Mock<ConfigLoader>("test-config-dir");
        var mockMqttCoordinatorInterface = new Mock<IMqttCoordinator>();
        
        _mockDeviceSettingsStore = new Mock<DeviceSettingsStore>(mockMqttCoordinatorInterface.Object);
        var mockNodeTelemetryStore = new Mock<NodeTelemetryStore>(mockMqttCoordinatorInterface.Object);
        _mockState = new Mock<State>(mockConfigLoader.Object, mockNodeTelemetryStore.Object);
        
        // Create dependencies for DeviceService
        var mockMqttCoordinatorConcrete = CreateMockMqttCoordinator();
        var globalEventDispatcher = new GlobalEventDispatcher();
        var mockDeviceServiceLogger = new Mock<ILogger<DeviceService>>();
        
        var deviceService = new DeviceService(_mockState.Object, mockMqttCoordinatorConcrete, globalEventDispatcher, mockDeviceServiceLogger.Object);
        
        _deviceController = new DeviceController(
            _mockLogger.Object,
            _mockDeviceSettingsStore.Object,
            deviceService,
            _mockState.Object
        );
    }

    [Test]
    public void Get_Should_Return_Device_Settings_By_Original_ID()
    {
        // Arrange
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:darrell",
            OriginalId = "keys:darrell",
            Name = "Darrell's Keys",
            RefRssi = -58
        };

        _mockDeviceSettingsStore
            .Setup(x => x.Get("keys:darrell"))
            .Returns(deviceSettings);

        // Act
        var result = _deviceController.Get("keys:darrell");

        // Assert
        Assert.That(result.settings, Is.Not.Null);
        Assert.That(result.settings.Id, Is.EqualTo("keys:darrell"));
        Assert.That(result.settings.Name, Is.EqualTo("Darrell's Keys"));
    }

    [Test]
    public void Get_Should_Return_Device_Settings_By_Aliased_ID()
    {
        // Arrange
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:dt-spare", // Aliased ID
            OriginalId = "keys:dt-spar", // Original MQTT topic
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Mock the DeviceSettingsStore to return settings when queried by aliased ID
        _mockDeviceSettingsStore
            .Setup(x => x.Get("keys:dt-spare"))
            .Returns(deviceSettings);

        // Act
        var result = _deviceController.Get("keys:dt-spare");

        // Assert
        Assert.That(result.settings, Is.Not.Null);
        Assert.That(result.settings.Id, Is.EqualTo("keys:dt-spare"));
        Assert.That(result.settings.OriginalId, Is.EqualTo("keys:dt-spar"));
        Assert.That(result.settings.Name, Is.EqualTo("Darrell Spare Keys"));
    }

    [Test]
    [Ignore("Needs to be updated for real instances")]
    public void Get_Should_Return_Details_From_State_When_Device_Exists()
    {
        // Arrange
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:darrell",
            OriginalId = "keys:darrell",
            Name = "Darrell's Keys",
            RefRssi = -58
        };

        // Create a real Device instance since GetDetails() is not virtual
        var testDevice = new Device("keys:darrell", null, TimeSpan.FromMinutes(5));
        
        var stateDevices = new System.Collections.Concurrent.ConcurrentDictionary<string, Device>();
        stateDevices.TryAdd("keys:darrell", testDevice);

        _mockDeviceSettingsStore
            .Setup(x => x.Get("keys:darrell"))
            .Returns(deviceSettings);

        _mockState
            .Setup(x => x.Devices)
            .Returns(stateDevices);

        // Act
        var result = _deviceController.Get("keys:darrell");

        // Assert
        Assert.That(result.settings, Is.Not.Null);
        Assert.That(result.details, Is.Not.Null);
        Assert.That(result.details.Count, Is.GreaterThan(0));
        Assert.That(result.details.Any(d => d.Key == "Best Scenario"), Is.True);
    }

    [Test]
    [Ignore("Needs to be updated for real instances")]
    public void Get_Should_Return_Default_Settings_When_Device_Not_Found()
    {
        // Arrange
        var deviceId = "nonexistent:device";
        
        _mockDeviceSettingsStore
            .Setup(x => x.Get(deviceId))
            .Returns((DeviceSettings)null);

        // Act
        var result = _deviceController.Get(deviceId);

        // Assert
        Assert.That(result.settings, Is.Not.Null);
        Assert.That(result.settings.Id, Is.EqualTo(deviceId));
        Assert.That(result.settings.OriginalId, Is.EqualTo(deviceId));
        Assert.That(result.settings.Name, Is.Null);
        Assert.That(result.details, Is.Not.Null);
        Assert.That(result.details.Count, Is.EqualTo(0));
    }

    [Test]
    [Ignore("Needs to be updated for real instances")]
    public void Get_Should_Handle_Aliased_Device_With_State_Data()
    {
        // Arrange - This tests the key scenario where calibration data 
        // should be accessible via the aliased ID
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:dt-spare", // Aliased ID
            OriginalId = "keys:dt-spar", // Original topic for calibration
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Create a real Device instance since GetDetails() is not virtual
        var testDevice = new Device("keys:dt-spare", null, TimeSpan.FromMinutes(5));

        // State data should be indexed by the aliased ID
        var stateDevices = new System.Collections.Concurrent.ConcurrentDictionary<string, Device>();
        stateDevices.TryAdd("keys:dt-spare", testDevice);

        _mockDeviceSettingsStore
            .Setup(x => x.Get("keys:dt-spare"))
            .Returns(deviceSettings);

        _mockState
            .Setup(x => x.Devices)
            .Returns(stateDevices);

        // Act
        var result = _deviceController.Get("keys:dt-spare");

        // Assert
        Assert.That(result.settings, Is.Not.Null);
        Assert.That(result.settings.Id, Is.EqualTo("keys:dt-spare"));
        Assert.That(result.settings.OriginalId, Is.EqualTo("keys:dt-spar"));
        
        // Device details should be available
        Assert.That(result.details, Is.Not.Null);
        Assert.That(result.details.Count, Is.GreaterThan(0));
        Assert.That(result.details.Any(d => d.Key == "Best Scenario"), Is.True);
    }

    [Test]
    [Ignore("Needs to be updated for real instances")]
    public async Task Set_Should_Call_DeviceSettingsStore_Set()
    {
        // Arrange
        var deviceId = "keys:test";
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:test",
            Name = "Test Device",
            RefRssi = -60
        };

        _mockDeviceSettingsStore
            .Setup(x => x.Set(deviceId, deviceSettings))
            .Returns(Task.CompletedTask);

        // Act
        await _deviceController.Set(deviceId, deviceSettings);

        // Assert
        _mockDeviceSettingsStore.Verify(x => x.Set(deviceId, deviceSettings), Times.Once);
    }

    private MqttCoordinator CreateMockMqttCoordinator()
    {
        // Create all required dependencies for MqttCoordinator
        var mockConfigLoader = new Mock<ConfigLoader>("test-config-dir");
        var mockLogger = new Mock<ILogger<MqttCoordinator>>();
        var mockMqttNetLogger = new Mock<MQTTnet.Diagnostics.IMqttNetLogger>();
        var mockSupervisorLogger = new Mock<ILogger<SupervisorConfigLoader>>();
        var supervisorConfigLoader = new SupervisorConfigLoader(mockSupervisorLogger.Object);
        
        return new MqttCoordinator(
            mockConfigLoader.Object,
            mockLogger.Object,
            mockMqttNetLogger.Object,
            supervisorConfigLoader
        );
    }
}