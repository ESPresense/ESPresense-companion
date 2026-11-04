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

    private class FixAt(Point3D fit) : ILocate
    {
        public bool Locate(Scenario scenario)
        {
            scenario.Confidence = 100;
            scenario.UpdateLocation(fit);
            return true;
        }
    }

    [Test]
    public async Task DeviceFilterIsFedTheRawFitNotTheScenariosSmoothedPosition()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg");
        Directory.CreateDirectory(workDir);
        var configLoader = new ConfigLoader(workDir);
        var mqttMock = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(),
            new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        mqttMock.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tracker = new DeviceTracker(state, mqttMock.Object, new TelemetryService(mqttMock.Object), new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqttMock.Object, state));
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(),
            new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader), new Mock<ILeaseService>().Object);

        var device = new Device("id", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(null, new FixAt(new Point3D(10, 0, 0)), "kitchen");
        scenario.UpdateLocation(new Point3D(0, 0, 0)); // the scenario's own filter starts at the origin
        device.Scenarios.Add(scenario);

        await locator.ProcessDevice(device);

        // A 10 m jump a moment later is mostly ignored by the scenario's filter. The device's filter is fresh, so it
        // starts wherever it's fed: at the raw fit, not at the scenario's already-smoothed position.
        Assert.That(scenario.Location.X, Is.LessThan(5));
        Assert.That(device.Location?.X, Is.EqualTo(10).Within(1e-6));
    }

    private class FixThenNoFix(Point3D fit) : ILocate
    {
        public int Passes;

        public bool Locate(Scenario scenario)
        {
            if (Passes++ == 0)
            {
                scenario.Confidence = 100;
                scenario.UpdateLocation(fit);
                return true;
            }

            // One node still hears it, so the scenario stays Current, but there's no fix this pass
            scenario.Confidence = 0;
            scenario.LastHit = DateTime.UtcNow;
            return false;
        }
    }

    [Test]
    public async Task DeviceFilterIsNotFedAnOldFitAgainWhenAPassFindsNoFix()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg");
        Directory.CreateDirectory(workDir);
        var configLoader = new ConfigLoader(workDir);
        var mqttMock = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(),
            new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        mqttMock.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tracker = new DeviceTracker(state, mqttMock.Object, new TelemetryService(mqttMock.Object), new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqttMock.Object, state));
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(),
            new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader), new Mock<ILeaseService>().Object);

        var device = new Device("id", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(null, new FixThenNoFix(new Point3D(3, 4, 0)), "kitchen");
        device.Scenarios.Add(scenario);

        await locator.ProcessDevice(device);
        var (_, afterFix) = device.KalmanFilter.GetPrediction();
        await Task.Delay(20);
        await locator.ProcessDevice(device);
        var (_, afterNoFix) = device.KalmanFilter.GetPrediction();

        // Fed again, the filter would grow surer of the old fix; left alone, it only grows less sure as time passes
        Assert.That(scenario.Current, Is.True);
        Assert.That(afterNoFix[0, 0], Is.GreaterThanOrEqualTo(afterFix[0, 0]));
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
