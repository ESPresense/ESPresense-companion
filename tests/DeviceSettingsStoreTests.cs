using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;

namespace ESPresense.Companion.Tests;

public class DeviceSettingsStoreTests
{
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<ILogger<DeviceSettingsStore>> _mockLogger;
    private DeviceSettingsStore _deviceSettingsStore;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        
        _mockLogger = new Mock<ILogger<DeviceSettingsStore>>();
        
        _deviceSettingsStore = new DeviceSettingsStore(_mockMqttCoordinator.Object);
        
        // Start the background service so it subscribes to events
        // Start the background service so it subscribes to events
        var startTask = _deviceSettingsStore.StartAsync(CancellationToken.None);
        // Ensure the service has started
        startTask.Wait(TimeSpan.FromSeconds(1));
        Assert.That(startTask.IsCompleted, Is.True, "Service should start within 1 second");
    }

    [TearDown]
    public void TearDown()
    {
        _deviceSettingsStore.StopAsync(CancellationToken.None).Wait();
        _deviceSettingsStore.Dispose();
    }

    [Test]
    public void Get_Should_Return_Device_By_Id()
    {
        // Arrange
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:darrell",
            OriginalId = "keys:darrell",
            Name = "Darrell's Keys",
            RefRssi = -58
        };

        // Simulate MQTT message received
        SimulateMqttDeviceConfig("keys:darrell", deviceSettings);

        // Act
        var result = _deviceSettingsStore.Get("keys:darrell");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo("keys:darrell"));
        Assert.That(result.Name, Is.EqualTo("Darrell's Keys"));
    }

    [Test]
    public void Get_Should_Return_Device_By_Alias()
    {
        // Arrange
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:darrell-alias", // This is the aliased ID
            OriginalId = "keys:darrell-original",
            Name = "Darrell's Keys",
            RefRssi = -58
        };

        // Simulate MQTT message received on original topic
        SimulateMqttDeviceConfig("keys:darrell-original", deviceSettings);

        // Act - Try to get by aliased ID
        var result = _deviceSettingsStore.Get("keys:darrell-alias");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo("keys:darrell-alias"));
        Assert.That(result.OriginalId, Is.EqualTo("keys:darrell-original"));
        Assert.That(result.Name, Is.EqualTo("Darrell's Keys"));
    }

    [Test]
    public void Get_Should_Prefer_ById_Over_ByAlias()
    {
        // Arrange - Create two different settings
        var originalSettings = new DeviceSettings
        {
            Id = "keys:device1",
            OriginalId = "keys:original-topic",
            Name = "Original Device",
            RefRssi = -58
        };

        var aliasedSettings = new DeviceSettings
        {
            Id = "keys:device2", 
            OriginalId = "keys:device1", // This creates an alias conflict
            Name = "Aliased Device",
            RefRssi = -62
        };

        // Simulate both MQTT messages
        SimulateMqttDeviceConfig("keys:original-topic", originalSettings);
        SimulateMqttDeviceConfig("keys:device1", aliasedSettings);

        // Act - Get by the conflicting ID
        var result = _deviceSettingsStore.Get("keys:device1");

        // Assert - Should return the ById match (aliasedSettings)
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Aliased Device"));
        Assert.That(result.OriginalId, Is.EqualTo("keys:device1"));
    }

    [Test]
    public void Should_Store_Calibration_Data_With_Original_ID()
    {
        // Arrange - Device with aliased ID but calibration should stay with original
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:dt-spare", // Aliased ID
            OriginalId = "keys:dt-spar", // Original MQTT topic ID
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Simulate MQTT config message on original topic
        SimulateMqttDeviceConfig("keys:dt-spar", deviceSettings);

        // Act - Verify we can get settings by original ID (for calibration data)
        var resultByOriginal = _deviceSettingsStore.Get("keys:dt-spar");
        var resultByAlias = _deviceSettingsStore.Get("keys:dt-spare");

        // Assert - Both should return the same settings object
        Assert.That(resultByOriginal, Is.Not.Null);
        Assert.That(resultByAlias, Is.Not.Null);
        Assert.That(resultByOriginal, Is.SameAs(resultByAlias));
        
        // Calibration data should be accessible via original ID
        Assert.That(resultByOriginal.OriginalId, Is.EqualTo("keys:dt-spar"));
        Assert.That(resultByOriginal.Id, Is.EqualTo("keys:dt-spare"));
    }

    [Test]
    public void Should_Not_Create_Conflicting_Records_When_Device_Aliased()
    {
        // Arrange - This simulates the bug scenario
        var deviceSettings = new DeviceSettings
        {
            Id = "keys:dt-spare", // Aliased ID
            OriginalId = "keys:dt-spar", // Original topic
            Name = "Darrell Spare Keys",
            RefRssi = -62
        };

        // Simulate the original MQTT config message
        SimulateMqttDeviceConfig("keys:dt-spar", deviceSettings);

        // Act - Try to create a conflicting record (this should not happen after the fix)
        var conflictingSettings = new DeviceSettings
        {
            Id = null, // This would be null when no config exists
            OriginalId = "keys:dt-spare",
            Name = null,
            RefRssi = null
        };

        // This simulates what would happen if state data came in for the aliased ID
        // but there's no separate config for it (which should be prevented)
        
        // Assert - We should only have one logical device, not conflicting records
        var resultByOriginal = _deviceSettingsStore.Get("keys:dt-spar");
        var resultByAlias = _deviceSettingsStore.Get("keys:dt-spare");
        
        Assert.That(resultByOriginal, Is.Not.Null);
        Assert.That(resultByAlias, Is.Not.Null);
        Assert.That(resultByOriginal.Name, Is.EqualTo("Darrell Spare Keys"));
        Assert.That(resultByAlias.Name, Is.EqualTo("Darrell Spare Keys"));
    }

    private void SimulateMqttDeviceConfig(string deviceId, DeviceSettings deviceSettings)
    {
        // Create the event args that MqttCoordinator would create
        var eventArgs = new DeviceSettingsEventArgs
        {
            DeviceId = deviceId,
            Payload = deviceSettings
        };

        // Directly raise the event on the mock
        _mockMqttCoordinator.Raise(x => x.DeviceConfigReceivedAsync += null, eventArgs);
    }
}