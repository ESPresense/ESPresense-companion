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
    public async Task PublishesFloorNameWhenCurrentScenarioHasNoRoom()
    {
        var directory = Path.Combine(Path.GetTempPath(), "floor-fallback-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var loader = new ConfigLoader(directory);
        await loader.ConfigAsync();
        var connection = new SQLiteAsyncConnection(":memory:");
        try
        {
            var mqtt = new Mock<MqttCoordinator>(loader, NullLogger<MqttCoordinator>.Instance,
                new MqttNetLogger(), new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
            mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
            var state = new State(loader, new NodeTelemetryStore(mqtt.Object));
            var events = new GlobalEventDispatcher();
            var tracker = new DeviceTracker(state, mqtt.Object, new TelemetryService(mqtt.Object), events,
                new DeviceSettingsStore(mqtt.Object, state));
            var locator = new MultiScenarioLocator(tracker, state, mqtt.Object, events,
                new DeviceHistoryStore(connection, loader), Mock.Of<ILeaseService>());
            var floor = new Floor();
            floor.Update(loader.Config!, new ConfigFloor { Id = "first", Name = "First floor" });
            var device = new Device("floor-only", null, TimeSpan.FromSeconds(30));
            device.Scenarios.Add(new Scenario(loader.Config, new DummyLocator(), "Floor only")
            {
                Floor = floor, Room = null, Confidence = 90, LastHit = DateTime.UtcNow
            });

            await locator.ProcessDevice(device);

            Assert.That(device.ReportedState, Is.EqualTo("First floor"));
            mqtt.Verify(m => m.TryEnqueueAsync("espresense/companion/floor-only", "First floor", true), Times.Once);
        }
        finally
        {
            await connection.CloseAsync();
            await loader.StopAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
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
        var leaseServiceMock = new Mock<ILeaseService>();

        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object);

        var device = new Device("id", null, TimeSpan.FromSeconds(1))
        {
            ReportedState = "kitchen"
        };

        var scenario = new Scenario(null, new DummyLocator(), "kitchen")
        {
            LastHit = DateTime.UtcNow.AddSeconds(-5)
        };
        device.Scenarios.Add(scenario);

        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home", true))
                .ReturnsAsync(true)
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
        var leaseServiceMock = new Mock<ILeaseService>();
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object);

        var device = new Device("anchored", null, TimeSpan.FromSeconds(1));
        var anchorLocation = new Point3D(5, 6, 1);
        device.SetAnchor(new DeviceAnchor(anchorLocation, null, null));
        state.Devices[device.Id] = device;

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", "not_home", true))
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
