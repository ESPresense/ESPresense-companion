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
    private const string DeviceId = "iBeacon:sparse";

    /// <summary>
    /// One floor, two rooms, three nodes. Nearest node is opt-in; node1 sits in Entry, while the
    /// three-node solve lands in Living Room. timeout: 1 expires readings after one second.
    /// </summary>
    private static string Yaml(string locator, bool nearestNode) => $"""
        timeout: 1
        floors:
          - id: first
            name: First Floor
            bounds: [[0, 0, 0], [10, 10, 3]]
            rooms:
              - name: Entry
                points: [[0, 0], [2, 0], [2, 2], [0, 2]]
              - name: Living Room
                points: [[2, 0], [10, 0], [10, 10], [0, 10], [0, 2], [2, 2]]
        nodes:
          - id: node1
            point: [1, 1, 1]
            floors: ["first"]
          - id: node2
            point: [9, 1, 1]
            floors: ["first"]
          - id: node3
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
            enabled: {locator == "multi_floor"}
          nadaraya_watson:
            enabled: {locator == "nadaraya_watson"}
          nearest_node:
            enabled: {nearestNode}
            max_distance: 10
        """;

    [TestCase("multi_floor")]
    [TestCase("nadaraya_watson")]
    public async Task OneLocator_DeviceHeardByOneNode_KeepsItsRoomInsteadOfGoingNotHome(string locator)
    {
        await using var h = await Harness.StartAsync(locator);

        // Three nodes hear it from the middle of the Living Room, so the locator has a fix.
        await h.HeardByAllThree();
        Assert.That(h.Device.Scenarios, Has.Count.EqualTo(1), $"only {locator} is enabled");

        await h.Locate();
        Assert.That(h.States, Is.EqualTo(new[] { "Living Room" }), "a three-node fix puts it in the Living Room");
        var scenario = h.Device.BestScenario!;
        var location = scenario.Location;
        var floor = scenario.Floor;

        // Two nodes stop reporting. One real second later their readings are stale and the previous
        // solve is older than timeout, which is the point the scenario used to expire.
        await Task.Delay(1200);
        await h.Heard("node1", 5.66);
        Assert.That(h.Device.CurrentReadings(), Has.Length.EqualTo(1), "only node1 is still current");

        await h.Locate();

        Assert.That(h.States, Does.Not.Contain(DeviceReport.NotHome), "a node is still hearing it");
        Assert.That(h.Reported, Is.EqualTo("Living Room"), "the last known room is better than none");
        Assert.That(h.Device.BestScenario, Is.Not.Null, "the scenario is still Current");
        Assert.That(h.Device.Confidence, Is.Zero, "one node is not a fix, so the map still hides it");
        Assert.That(h.Device.Room?.Name, Is.EqualTo("Living Room"));
        Assert.That(scenario.Location, Is.EqualTo(location), "a sparse pass must not invent a position");
        Assert.That(scenario.Floor, Is.SameAs(floor));
        Assert.That(scenario.LastHit, Is.EqualTo(h.Device.Nodes["node1"].LastHit), "presence uses the reading's timestamp");
        Assert.That(scenario.Minimum, Is.EqualTo(5.66));
        Assert.That(scenario.Fixes, Is.EqualTo(1));

        await h.HeardByAllThree();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo("Living Room"));
        Assert.That(h.Device.Confidence, Is.GreaterThan(0), "fresh readings allow a solve after a sparse pass");
        Assert.That(scenario.Fixes, Is.EqualTo(3));
    }

    [TestCase("multi_floor", false)]
    [TestCase("multi_floor", true)]
    [TestCase("nadaraya_watson", false)]
    [TestCase("nadaraya_watson", true)]
    public async Task OneLocator_NothingHearsTheDevice_StillGoesNotHome(string locator, bool afterSparsePass)
    {
        await using var h = await Harness.StartAsync(locator);

        await h.HeardByAllThree();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo("Living Room"));

        if (afterSparsePass)
        {
            await Task.Delay(1200);
            await h.Heard("node1", 5.66);
            await h.Locate();
            Assert.That(h.Reported, Is.EqualTo("Living Room"));
            Assert.That(h.Device.Confidence, Is.Zero);
        }

        // Nothing reports it again: every reading goes stale and the device really has left.
        await Task.Delay(1200);
        await h.Locate();

        Assert.That(h.Reported, Is.EqualTo(DeviceReport.NotHome));
        Assert.That(h.Device.BestScenario, Is.Null);
        var scenario = h.Device.Scenarios.Single();
        Assert.That(scenario.LastHit, Is.Null);
        Assert.That(scenario.Minimum, Is.Null);
        Assert.That(scenario.Fixes, Is.Zero);
    }

    [TestCase("multi_floor")]
    [TestCase("nadaraya_watson")]
    public async Task OneLocator_OneNodeWithoutPreviousFix_DoesNotInventAPlacement(string locator)
    {
        await using var h = await Harness.StartAsync(locator);
        await h.Heard("node1", 5.66);
        var scenario = h.Device.Scenarios.Single();
        var location = scenario.Location;

        await h.Locate();

        Assert.That(h.Device.BestScenario, Is.SameAs(scenario), "the reading is current even without a fix");
        Assert.That(scenario.LastHit, Is.EqualTo(h.Device.Nodes["node1"].LastHit));
        Assert.That(scenario.Confidence, Is.Zero);
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
        Assert.That(scenario.Location, Is.EqualTo(location));
        Assert.That(h.Reported, Is.EqualTo(DeviceReport.NotHome), "there is no last known room to retain");
    }

    [TestCase("multi_floor")]
    [TestCase("nadaraya_watson")]
    public async Task NearestNode_OnlyTakesOverWhenThePrimaryHasNoPosition(string locator)
    {
        await using var h = await Harness.StartAsync(locator, nearestNode: true);
        // Freeze the priors to prove handoff is an eligibility rule, not a confidence/smoothing race.
        h.Filtering.SmoothingWeight = 1;
        await h.Heard("node1", 5.66);
        Assert.That(h.Device.Scenarios, Has.Count.EqualTo(2));
        var primary = h.Device.Scenarios.Single(s => !s.IsNearestNode);
        var nearest = h.Device.Scenarios.Single(s => s.IsNearestNode);

        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(nearest), "cold start can use the explicit fallback");
        Assert.That(h.Reported, Is.EqualTo("Entry"));

        nearest.Probability = 1;
        primary.Probability = 0;
        await h.HeardByAllThree();
        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(primary), "a recovered solve immediately displaces fallback");
        Assert.That(h.Reported, Is.EqualTo("Living Room"));
        Assert.That(nearest.WeightedConfidence, Is.Zero, "fallback cannot influence fusion while a position exists");

        primary.Probability = 1;
        nearest.Probability = 0;
        h.ExpireReadings();
        await h.Heard("node1", 5.66);
        await h.Locate();
        Assert.That(primary.Confidence, Is.Zero);
        Assert.That(primary.Room?.Name, Is.EqualTo("Living Room"));
        Assert.That(h.Device.BestScenario, Is.SameAs(nearest), "fallback immediately displaces a retained room");
        Assert.That(h.Reported, Is.EqualTo("Entry"));
        Assert.That(primary.WeightedConfidence, Is.Zero);

        await h.HeardByAllThree();
        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(primary));
        Assert.That(h.Reported, Is.EqualTo("Living Room"));

        h.ExpireReadings();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo(DeviceReport.NotHome));
        Assert.That(h.Device.BestScenario, Is.Null, "neither locator can extend expired readings");
    }

    /// <summary>The real tracker, fusion and publisher over a config, with the state topic recorded.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly YamlStateFixture _fx;
        private readonly DeviceTracker _tracker;
        private readonly DeviceStatePublisher _publisher;
        private readonly MultiScenarioLocator _locator;

        /// <summary>Every state topic payload published for the device, in order.</summary>
        public List<string?> States { get; } = new();

        private Harness(YamlStateFixture fx, DeviceTracker tracker, DeviceStatePublisher publisher, MultiScenarioLocator locator)
        {
            _fx = fx;
            _tracker = tracker;
            _publisher = publisher;
            _locator = locator;
        }

        public static async Task<Harness> StartAsync(string locator, bool nearestNode = false)
        {
            var fx = await YamlStateFixture.LoadAsync(Yaml(locator, nearestNode));
            var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
            var mqtt = new Mock<MqttCoordinator>(fx.Loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
            var tracker = new DeviceTracker(fx.State, mqtt.Object, new TelemetryService(mqtt.Object), new GlobalEventDispatcher(),
                new DeviceSettingsStore(mqtt.Object, fx.State));
            var publisher = new DeviceStatePublisher(mqtt.Object);
            var h = new Harness(fx, tracker, publisher,
                new MultiScenarioLocator(tracker, fx.State, publisher, Mock.Of<IDeviceHistoryWriter>(),
                    new GlobalEventDispatcher(), Mock.Of<ILeaseService>(), NullLogger<MultiScenarioLocator>.Instance));

            mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
            // After the catch-all, so this one wins for the state topic.
            mqtt.Setup(m => m.TryEnqueueAsync(MqttTopics.CompanionDeviceState(DeviceId), It.IsAny<string?>(), true))
                .Callback((string _, string? payload, bool _) => h.States.Add(payload))
                .ReturnsAsync(true);
            mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

            return h;
        }

        public Device Device => _fx.State.Devices[DeviceId];
        public ConfigFiltering Filtering => _fx.State.Config!.Filtering;

        public void ExpireReadings()
        {
            foreach (var reading in Device.Nodes.Values)
                reading.LastHit = DateTime.UtcNow.AddMinutes(-1);
        }

        /// <summary>The last state topic payload the publisher recorded as accepted.</summary>
        public string? Reported => _publisher.Reported(Device).State;

        public Task Locate() => _locator.ProcessDevice(Device);

        /// <summary>Distances from the three nodes to the middle of the room.</summary>
        public async Task HeardByAllThree()
        {
            await Heard("node1", 5.66);
            await Heard("node2", 5.66);
            await Heard("node3", 4.0);
        }

        public Task Heard(string nodeId, double distance) =>
            _tracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs
            {
                NodeId = nodeId,
                DeviceId = DeviceId,
                Payload = new DeviceMessage { Rssi = -65, RefRssi = -59, Distance = distance }
            });

        public ValueTask DisposeAsync() => _fx.DisposeAsync();
    }
}
