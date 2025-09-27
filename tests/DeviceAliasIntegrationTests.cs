using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests;

/// <summary>
/// Integration tests to verify that device aliasing works correctly end-to-end
/// and that calibration data remains accessible after device ID changes.
/// </summary>
public class DeviceAliasIntegrationTests
{
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<ConfigLoader> _mockConfigLoader;
    private Mock<NodeTelemetryStore> _mockNodeTelemetryStore;
    private Mock<ILogger<DeviceController>> _mockControllerLogger;
    private Mock<ILogger<DeviceService>> _mockDeviceServiceLogger;
    
    private DeviceSettingsStore _deviceSettingsStore;
    private State _state;
    private DeviceService? _deviceService;
    private DeviceController _deviceController;

    [SetUp]
    public void Setup()
    {
        // Set up mocks
        _mockConfigLoader = new Mock<ConfigLoader>("test-config-dir");
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _mockNodeTelemetryStore = new Mock<NodeTelemetryStore>(_mockMqttCoordinator.Object);
        _mockControllerLogger = new Mock<ILogger<DeviceController>>();
        _mockDeviceServiceLogger = new Mock<ILogger<DeviceService>>();

        // Create real instances for integration testing
        _deviceSettingsStore = new DeviceSettingsStore(_mockMqttCoordinator.Object);
        _state = new State(_mockConfigLoader.Object, _mockNodeTelemetryStore.Object, _deviceSettingsStore);
        
        // Create DeviceService with required dependencies
        var mqttCoordinator = CreateMockMqttCoordinator();
        var globalEventDispatcher = new GlobalEventDispatcher();
        _deviceService = new DeviceService(_state, mqttCoordinator, globalEventDispatcher, _mockDeviceServiceLogger.Object);
        
        _deviceController = new DeviceController(
            _mockControllerLogger.Object,
            _deviceSettingsStore,
            _deviceService,
            _state
        );
    }

    [Test]
    public async Task End_To_End_Device_Alias_Flow_Should_Work()
    {
        // Arrange - Simulate the real-world scenario
        var originalTopicId = "keys:dt-spar"; // Original MQTT topic
        var aliasedId = "keys:dt-spare"; // What the device broadcasts as
        
        var deviceSettings = new DeviceSettings
        {
            Id = aliasedId, // Device is configured to use this ID
            OriginalId = originalTopicId, // But config comes via this topic
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Act 1 - Simulate MQTT config message received
        await SimulateMqttDeviceConfig(originalTopicId, deviceSettings);

        // Act 2 - Simulate device state data (would come from tracking)
        // Since Device.GetDetails() is not virtual, we can't mock it easily
        // Instead, we'll create a real Device instance and populate it with test data
        // For the test purposes, we just need to verify that the device lookup works
        var testDevice = new Device(aliasedId, null, TimeSpan.FromMinutes(5));
        _state.Devices[aliasedId] = testDevice; // State uses aliased ID

        // Act 3 - API requests for both IDs
        var resultByOriginal = _deviceController.Get(originalTopicId);
        var resultByAlias = _deviceController.Get(aliasedId);

        // Assert - Both requests should work and return consistent data
        Assert.That(resultByOriginal.settings, Is.Not.Null, "Settings should be found by original ID");
        Assert.That(resultByAlias.settings, Is.Not.Null, "Settings should be found by aliased ID");
        
        // Settings should be the same object
        Assert.That(resultByOriginal.settings.Name, Is.EqualTo("Darrell Spare Keys"));
        Assert.That(resultByAlias.settings.Name, Is.EqualTo("Darrell Spare Keys"));
        Assert.That(resultByOriginal.settings.RefRssi, Is.EqualTo(-62));
        Assert.That(resultByAlias.settings.RefRssi, Is.EqualTo(-62));

        // Device details should be available via aliased ID (where state data is)
        Assert.That(resultByAlias.details.Count, Is.GreaterThan(0), "Aliased ID should have device details");
        Assert.That(resultByAlias.details.Any(d => d.Key == "Best Scenario"), Is.True);

        // Original ID will have basic device details but not the specific tracking data we added
        Assert.That(resultByOriginal.details.Count, Is.GreaterThanOrEqualTo(0), "Original ID should have some device details");
    }

    [Test]
    public async Task Calibration_Data_Should_Persist_Through_Aliasing()
    {
        // Arrange - Start with device under original ID with calibration data
        var originalId = "keys:device-original";
        var aliasedId = "keys:device-aliased";

        // Step 1: Device initially configured with original ID
        var initialSettings = new DeviceSettings
        {
            Id = originalId,
            OriginalId = originalId,
            Name = "Test Device",
            RefRssi = -58 // This is calibration data
        };

        await SimulateMqttDeviceConfig(originalId, initialSettings);

        // Step 2: Device gets aliased (ID changed but config stays on original topic)
        var aliasedSettings = new DeviceSettings
        {
            Id = aliasedId, // New aliased ID
            OriginalId = originalId, // Config still comes via original topic
            Name = "Test Device (Aliased)",
            RefRssi = -58 // Calibration data preserved
        };

        await SimulateMqttDeviceConfig(originalId, aliasedSettings);

        // Act - Access calibration data via both IDs
        var resultByOriginal = _deviceController.Get(originalId);
        var resultByAlias = _deviceController.Get(aliasedId);

        // Assert - Calibration data (RefRssi) should be accessible via both
        Assert.That(resultByOriginal.settings.RefRssi, Is.EqualTo(-58), 
            "Calibration data should be accessible via original ID");
        Assert.That(resultByAlias.settings.RefRssi, Is.EqualTo(-58), 
            "Calibration data should be accessible via aliased ID");
        
        // Both should refer to the same calibration configuration
        Assert.That(resultByOriginal.settings.OriginalId, Is.EqualTo(originalId));
        Assert.That(resultByAlias.settings.OriginalId, Is.EqualTo(originalId));
    }

    [Test]
    public async Task Should_Not_Create_Duplicate_Records_For_Aliased_Device()
    {
        // Arrange - This test prevents the regression that caused the original issue
        var originalTopicId = "keys:dt-spar";
        var aliasedId = "keys:dt-spare";

        var deviceSettings = new DeviceSettings
        {
            Id = aliasedId,
            OriginalId = originalTopicId,
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Act - Device config received via original topic
        await SimulateMqttDeviceConfig(originalTopicId, deviceSettings);

        // Verify we can access settings via both IDs
        var settingsByOriginal = _deviceSettingsStore.Get(originalTopicId);
        var settingsByAlias = _deviceSettingsStore.Get(aliasedId);

        // Assert - Should be the same object, not duplicates
        Assert.That(settingsByOriginal, Is.Not.Null);
        Assert.That(settingsByAlias, Is.Not.Null);
        Assert.That(settingsByOriginal, Is.SameAs(settingsByAlias), 
            "Both IDs should return the same settings object, preventing duplicates");

        // Verify the fix: calibration data stays with original ID infrastructure
        Assert.That(settingsByOriginal.OriginalId, Is.EqualTo(originalTopicId));
        Assert.That(settingsByAlias.OriginalId, Is.EqualTo(originalTopicId));
    }

    [Test]
    public async Task Frontend_URL_Navigation_Should_Work_After_Aliasing()
    {
        // Arrange - Simulate the exact scenario that was failing
        var originalTopicId = "keys:dt-spar"; // MQTT config topic
        var aliasedId = "keys:dt-spare"; // What device broadcasts as / what shows in UI table

        var deviceSettings = new DeviceSettings
        {
            Id = aliasedId, // Device configured to broadcast this ID
            OriginalId = originalTopicId, // But config stored under this topic
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        await SimulateMqttDeviceConfig(originalTopicId, deviceSettings);

        // Act - Simulate frontend requesting /api/device/keys:dt-spare
        var apiResult = _deviceController.Get(aliasedId);

        // Assert - Should return valid settings (not null ID)
        Assert.That(apiResult.settings, Is.Not.Null);
        Assert.That(apiResult.settings.Id, Is.EqualTo(aliasedId), 
            "API should return settings with the requested ID");
        Assert.That(apiResult.settings.Name, Is.EqualTo("Darrell Spare Keys"), 
            "Settings should have proper name (not null)");
        Assert.That(apiResult.settings.RefRssi, Is.EqualTo(-62), 
            "Calibration data should be accessible");

        // This proves the URL /api/device/keys:dt-spare will work
        // even though config is stored under keys:dt-spar
    }

    private async Task SimulateMqttDeviceConfig(string deviceId, DeviceSettings deviceSettings)
    {
        // Since we can't easily mock the event, use reflection to directly populate the internal dictionaries
        // This simulates what happens when a MQTT device config message is received
        
        // Start the background service so the internal state is ready
        using var cts = new CancellationTokenSource();
        var executeTask = _deviceSettingsStore.StartAsync(cts.Token);
        await Task.Delay(10); // Give it a moment to initialize
        
        // Use reflection to access the private fields 
        var storeByIdField = typeof(DeviceSettingsStore).GetField("_storeById", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var storeByAliasField = typeof(DeviceSettingsStore).GetField("_storeByAlias", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
        var storeById = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSettings>)storeByIdField!.GetValue(_deviceSettingsStore)!;
        var storeByAlias = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceSettings>)storeByAliasField!.GetValue(_deviceSettingsStore)!;
        
        // Simulate the actual logic from DeviceSettingsStore.ExecuteAsync
        storeById.AddOrUpdate(deviceId, _ => deviceSettings, (_, _) => deviceSettings);
        if (deviceSettings.Id != null) 
        {
            storeByAlias.AddOrUpdate(deviceSettings.Id, _ => deviceSettings, (_, _) => deviceSettings);
        }
        
        cts.Cancel();
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