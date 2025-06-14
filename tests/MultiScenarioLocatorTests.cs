using ESPresense.Models;
using ESPresense.Locators;
using ESPresense.Services;
using ESPresense.Utils;
using ESPresense.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SQLite;

namespace ESPresense.Companion.Tests;

public class MultiScenarioLocatorTests
{
    private class DummyLocator : ILocate
    {
        public bool Locate(Scenario scenario) => true;
    }

    [Test]
    public async Task NotHomeStateWhenAllScenariosExpire()
    {
        // Arrange minimal environment
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg");
        Directory.CreateDirectory(workDir);

        var configLoader = new ConfigLoader(workDir);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);

        var mqttMock = new Mock<MqttCoordinator>(configLoader,
            NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(),
            supervisor);

        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object);
        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object), deviceSettingsStore);
        var tele = new TelemetryService(mqttMock.Object);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher());
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);

        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history);

        var device = new Device("id", null, TimeSpan.FromSeconds(1))
        {
            ReportedState = "kitchen"
        };

        var scenario = new Scenario(null, new DummyLocator(), "kitchen")
        {
            LastHit = DateTime.UtcNow.AddSeconds(-5)
        };
        device.Scenarios.Add(scenario);

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", "not_home", false))
                .Returns(Task.CompletedTask)
                .Verifiable();

        // Act
        await locator.ProcessDevice(device);

        // Assert
        Assert.That(device.ReportedState, Is.EqualTo("not_home"));
        mqttMock.Verify();
    }
}

