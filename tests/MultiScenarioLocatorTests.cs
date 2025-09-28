using ESPresense.Models;
using ESPresense.Locators;
using ESPresense.Services;
using ESPresense.Utils;
using ESPresense.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SQLite;
using MathNet.Spatial.Euclidean;

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

        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
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

    [Test]
    public async Task AnchoredDevicePublishesFixedAttributes()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor");
        Directory.CreateDirectory(workDir);

        var configLoader = new ConfigLoader(workDir);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);

        var mqttMock = new Mock<MqttCoordinator>(configLoader,
            NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(),
            supervisor)
        { CallBase = true };

        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history);

        var device = new Device("anchored", null, TimeSpan.FromSeconds(1));
        var anchorLocation = new Point3D(5, 6, 1);
        device.SetAnchor(new DeviceAnchor(anchorLocation, null, null));
        state.Devices[device.Id] = device;

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", "not_home", false))
            .Returns(Task.CompletedTask)
            .Verifiable();

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true))
            .Returns(Task.CompletedTask)
            .Verifiable();

        await locator.ProcessDevice(device);

        Assert.That(device.ReportedLocation, Is.EqualTo(anchorLocation));
        Assert.That(device.BestScenario, Is.Null);

        mqttMock.Verify();
    }
}
