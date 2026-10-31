using ESPresense.Controllers;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests;

public class DeviceTrackerTests
{
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
    }
}
