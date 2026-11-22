using ESPresense.Companion.Tests.Models;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// A device that drops to one current reading has no fix, but it is still here: a node is still
/// hearing it. The locators that bail out early have to keep their scenario Current anyway, because
/// ScenarioFusion only picks BestScenario from Current scenarios and a null BestScenario publishes
/// not_home. Reported on ESPresense#2220 as devices leaving the map and coming back "at random" on a
/// config with multi_floor as the only enabled locator.
/// </summary>
public class SparseReadingPresenceTests
{
    // timeout: 1 so one real second of staleness expires a reading, and the scenario with it.
    private const string Yaml = """
        timeout: 1
        floors:
          - id: first
            name: First Floor
            bounds: [[0, 0, 0], [10, 10, 3]]
            rooms:
              - name: Living Room
                points: [[0, 0], [10, 0], [10, 10], [0, 10]]
        nodes:
          - id: node1
            name: Node 1
            point: [1, 1, 1]
            floors: ["first"]
          - id: node2
            name: Node 2
            point: [9, 1, 1]
            floors: ["first"]
          - id: node3
            name: Node 3
            point: [5, 9, 1]
            floors: ["first"]
        locators:
          nelder_mead:
            enabled: false
          bfgs:
            enabled: false
          mle:
            enabled: false
          multi_floor:
            enabled: true
          nadaraya_watson:
            enabled: false
          nearest_node:
            enabled: false
        """;

    private const string DeviceId = "iBeacon:sparse";

    [Test]
    public async Task MultiFloorOnly_DeviceHeardByOneNode_KeepsItsRoomInsteadOfGoingNotHome()
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml);

        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(fx.Loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
        var states = new List<string?>();
        mqtt.Setup(m => m.TryEnqueueAsync(MqttTopics.CompanionDeviceState(DeviceId), It.IsAny<string?>(), true))
            .Callback((string _, string? payload, bool _) => states.Add(payload))
            .ReturnsAsync(true);
        mqtt.Setup(m => m.TryEnqueueAsync(MqttTopics.DeviceAttributes(DeviceId), It.IsAny<string?>(), true))
            .ReturnsAsync(true);
        mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var tele = new TelemetryService(mqtt.Object);
        var tracker = new DeviceTracker(fx.State, mqtt.Object, tele, new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqtt.Object, fx.State));
        var publisher = new DeviceStatePublisher(mqtt.Object);
        var locator = new MultiScenarioLocator(tracker, fx.State, publisher, Mock.Of<IDeviceHistoryWriter>(),
            new GlobalEventDispatcher(), Mock.Of<ILeaseService>(), NullLogger<MultiScenarioLocator>.Instance);

        // Three nodes hear it from the middle of the Living Room, so MultiFloor has a fix.
        await Heard(tracker, "node1", 5.66);
        await Heard(tracker, "node2", 5.66);
        await Heard(tracker, "node3", 4.0);

        var device = fx.State.Devices[DeviceId];
        Assert.That(device.Scenarios.Select(s => s.Name), Is.EqualTo(new[] { "MultiFloor" }), "only multi_floor is enabled");

        await locator.ProcessDevice(device);
        Assert.That(states, Is.EqualTo(new[] { "Living Room" }), "a three-node fix puts it in the Living Room");

        // Two nodes stop reporting. One real second later their readings are stale and the previous
        // solve is older than timeout, which is the point the scenario used to expire.
        await Task.Delay(1200);
        await Heard(tracker, "node1", 5.66);
        Assert.That(device.CurrentReadings(), Has.Length.EqualTo(1), "only node1 is still current");

        await locator.ProcessDevice(device);

        Assert.That(states, Does.Not.Contain(DeviceReport.NotHome), "a node is still hearing it");
        Assert.That(publisher.Reported(device).State, Is.EqualTo("Living Room"));
        Assert.That(device.BestScenario, Is.Not.Null, "the scenario is still Current");
        Assert.That(device.Confidence, Is.Zero, "one node is not a fix, so the map still hides it");
        Assert.That(device.Room?.Name, Is.EqualTo("Living Room"), "the last known room is better than none");
    }

    [Test]
    public async Task MultiFloorOnly_NothingHearsTheDevice_StillGoesNotHome()
    {
        await using var fx = await YamlStateFixture.LoadAsync(Yaml);

        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(fx.Loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
        mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
        mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var tele = new TelemetryService(mqtt.Object);
        var tracker = new DeviceTracker(fx.State, mqtt.Object, tele, new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqtt.Object, fx.State));
        var publisher = new DeviceStatePublisher(mqtt.Object);
        var locator = new MultiScenarioLocator(tracker, fx.State, publisher, Mock.Of<IDeviceHistoryWriter>(),
            new GlobalEventDispatcher(), Mock.Of<ILeaseService>(), NullLogger<MultiScenarioLocator>.Instance);

        await Heard(tracker, "node1", 5.66);
        await Heard(tracker, "node2", 5.66);
        await Heard(tracker, "node3", 4.0);

        var device = fx.State.Devices[DeviceId];
        await locator.ProcessDevice(device);
        Assert.That(publisher.Reported(device).State, Is.EqualTo("Living Room"));

        // Nothing reports it again: every reading goes stale and the device really has left.
        await Task.Delay(1200);
        await locator.ProcessDevice(device);

        Assert.That(publisher.Reported(device).State, Is.EqualTo(DeviceReport.NotHome));
        Assert.That(device.BestScenario, Is.Null);
    }

    private static Task Heard(DeviceTracker tracker, string nodeId, double distance) =>
        tracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs
        {
            NodeId = nodeId,
            DeviceId = DeviceId,
            Payload = new DeviceMessage { Rssi = -65, RefRssi = -59, Distance = distance }
        });
}
