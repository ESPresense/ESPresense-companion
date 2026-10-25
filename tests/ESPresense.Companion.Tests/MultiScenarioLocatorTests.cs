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

    [Test]
    public async Task AnchoredDeviceReportsAnchorRoomAsState()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor-room");
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

        var config = new Config();
        var floor = new Floor();
        floor.Update(config, new ConfigFloor
        {
            Id = "first",
            Name = "First Floor",
            Bounds = new[] { new double[] { 0, 0, 0 }, new double[] { 10, 10, 3 } }
        });
        var room = new Room();
        room.Update(config, floor, new ConfigRoom
        {
            Id = "living",
            Name = "Living Room",
            Points = new[] { new double[] { 0, 0 }, new double[] { 5, 0 }, new double[] { 5, 5 }, new double[] { 0, 5 } }
        });

        var device = new Device("anchored", null, TimeSpan.FromSeconds(30)) { ReportedState = "not_home" };
        device.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), floor, room));
        HeardBy(device, "node1");

        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", "Living Room", true))
            .ReturnsAsync(true)
            .Verifiable(Times.Once);
        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true))
            .ReturnsAsync(true);

        await locator.ProcessDevice(device);
        await locator.ProcessDevice(device);

        Assert.That(device.ReportedState, Is.EqualTo("Living Room"));
        mqttMock.Verify(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", It.IsAny<string>(), true), Times.Once);
        mqttMock.Verify();
    }

    [Test]
    public async Task AnchoringRepublishesAttributesWhenRoomAndPositionAreUnchanged()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor-transition");
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

        var config = new Config();
        var floor = new Floor();
        floor.Update(config, new ConfigFloor
        {
            Id = "first",
            Name = "First Floor",
            Bounds = new[] { new double[] { 0, 0, 0 }, new double[] { 10, 10, 3 } }
        });
        var room = new Room();
        room.Update(config, floor, new ConfigRoom
        {
            Id = "living",
            Name = "Living Room",
            Points = new[] { new double[] { 0, 0 }, new double[] { 5, 0 }, new double[] { 5, 5 }, new double[] { 0, 5 } }
        });

        // Last located report: Living Room at exactly the spot the device is then anchored to.
        var anchorLocation = new Point3D(1, 1, 1);
        var device = new Device("anchored", null, TimeSpan.FromSeconds(30)) { ReportedState = "Living Room", ReportedLocation = anchorLocation };
        device.SetAnchor(new DeviceAnchor(anchorLocation, floor, room));
        HeardBy(device, "node1");

        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", "Living Room", true))
            .ReturnsAsync(true);
        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", It.Is<string>(p => p.Contains("\"best_scenario\":\"Anchored\"")), true))
            .ReturnsAsync(true)
            .Verifiable(Times.Once);

        await locator.ProcessDevice(device);
        await locator.ProcessDevice(device);

        mqttMock.Verify();
    }

    [Test]
    public async Task AnchoredDeviceNoNodeHearsReportsNotHome()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor-absent");
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

        var config = new Config();
        var floor = new Floor();
        floor.Update(config, new ConfigFloor
        {
            Id = "first",
            Name = "First Floor",
            Bounds = new[] { new double[] { 0, 0, 0 }, new double[] { 10, 10, 3 } }
        });
        var room = new Room();
        room.Update(config, floor, new ConfigRoom
        {
            Id = "living",
            Name = "Living Room",
            Points = new[] { new double[] { 0, 0 }, new double[] { 5, 0 }, new double[] { 5, 5 }, new double[] { 0, 5 } }
        });

        // Anchored in the Living Room, but the last hit is older than the timeout: the beacon is gone.
        var device = new Device("anchored", null, TimeSpan.FromSeconds(30)) { ReportedState = "Living Room" };
        device.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), floor, room));
        HeardBy(device, "node1", DateTime.UtcNow.AddMinutes(-5));

        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home", true))
            .ReturnsAsync(true)
            .Verifiable(Times.Once);
        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true))
            .ReturnsAsync(true);

        await locator.ProcessDevice(device);

        Assert.That(device.ReportedState, Is.EqualTo("not_home"));
        mqttMock.Verify();
    }

    [Test]
    public async Task AnchoredAttributesRetryAfterFailedPublish()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor-retry");
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

        // Already reported at the anchor spot; the beacon then goes quiet, so only the state changes.
        var anchorLocation = new Point3D(1, 1, 1);
        var device = new Device("anchored", null, TimeSpan.FromSeconds(30));
        device.SetAnchor(new DeviceAnchor(anchorLocation, null, null));
        device.ReportedState = "First Floor";
        device.ReportedLocation = anchorLocation;

        mqttMock.Setup(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home", true))
            .ReturnsAsync(true);
        mqttMock.SetupSequence(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await locator.ProcessDevice(device);
        await locator.ProcessDevice(device);
        await locator.ProcessDevice(device);

        Assert.That(device.ReportedState, Is.EqualTo("not_home"));
        mqttMock.Verify(m => m.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true), Times.Exactly(2));
    }

    private static void HeardBy(Device device, string nodeId, DateTime? lastHit = null)
    {
        device.Nodes[nodeId] = new DeviceToNode(device, new Node(nodeId, NodeSourceType.Config)) { LastHit = lastHit ?? DateTime.UtcNow };
    }
}
