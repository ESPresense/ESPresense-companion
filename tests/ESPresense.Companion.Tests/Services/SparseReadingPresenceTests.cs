using ESPresense.Companion.Tests.Models;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>Observable fusion and presence behavior from specs/fusion.md.</summary>
public class SparseReadingPresenceTests
{
    private const string DeviceId = "iBeacon:sparse";

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
          - id: node4
            point: [9, 9, 2.5]
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

        await h.HeardByAllFour();
        Assert.That(h.Device.Scenarios, Has.Count.EqualTo(1), $"only {locator} is enabled");

        await h.Locate();
        Assert.That(h.States, Is.EqualTo(new[] { "Living Room" }), "a four-node fix puts it in the Living Room");
        var scenario = h.Device.BestScenario!;
        var location = scenario.Location;
        var floor = scenario.Floor;

        // Let the previous readings exceed timeout before refreshing node1.
        await Task.Delay(1200);
        await h.Heard("node1", 2.83);
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
        Assert.That(scenario.Minimum, Is.EqualTo(2.83));
        Assert.That(scenario.Fixes, Is.EqualTo(1));

        await h.HeardByAllFour();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo("Living Room"));
        Assert.That(h.Device.Confidence, Is.GreaterThan(0), "fresh readings allow a solve after a sparse pass");
        Assert.That(scenario.Fixes, Is.EqualTo(4));
    }

    [TestCase("multi_floor", false)]
    [TestCase("multi_floor", true)]
    [TestCase("nadaraya_watson", false)]
    [TestCase("nadaraya_watson", true)]
    public async Task OneLocator_NothingHearsTheDevice_StillGoesNotHome(string locator, bool afterSparsePass)
    {
        await using var h = await Harness.StartAsync(locator);

        await h.HeardByAllFour();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo("Living Room"));

        if (afterSparsePass)
        {
            await Task.Delay(1200);
            await h.Heard("node1", 2.83);
            await h.Locate();
            Assert.That(h.Reported, Is.EqualTo("Living Room"));
            Assert.That(h.Device.Confidence, Is.Zero);
        }

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
        await h.Heard("node1", 2.83);
        var scenario = h.Device.Scenarios.Single();
        var location = scenario.Location;

        await h.Locate();

        Assert.That(scenario.Current, Is.True, "the reading is current even without a fix");
        Assert.That(h.Device.BestScenario, Is.Null, "a never-placed scenario is not a position estimate");
        Assert.That(scenario.LastHit, Is.EqualTo(h.Device.Nodes["node1"].LastHit));
        Assert.That(scenario.Confidence, Is.Zero);
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
        Assert.That(scenario.Location, Is.EqualTo(location));
        Assert.That(h.Reported, Is.EqualTo(DeviceReport.NotHome), "there is no last known room to retain");

        await h.HeardByAllFour();
        await h.Locate();
        Assert.That(h.Device.KalmanFilter.Location, Is.EqualTo(h.Device.BestScenario!.Location),
            "the first real measurement must seed the device filter without a default-origin prior");
    }

    [TestCase("multi_floor")]
    [TestCase("nadaraya_watson")]
    public async Task NearestNode_OnlyTakesOverWhenThePrimaryHasNoPosition(string locator)
    {
        await using var h = await Harness.StartAsync(locator, nearestNode: true);
        // Freeze the priors to prove handoff is an eligibility rule, not a confidence/smoothing race.
        h.Filtering.SmoothingWeight = 1;
        await h.Heard("node1", 2.83);
        Assert.That(h.Device.Scenarios, Has.Count.EqualTo(2));
        var primary = h.Device.Scenarios.Single(s => !s.IsNearestNode);
        var nearest = h.Device.Scenarios.Single(s => s.IsNearestNode);

        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(nearest), "cold start can use the explicit fallback");
        Assert.That(h.Reported, Is.EqualTo("Entry"));

        nearest.Probability = 1;
        primary.Probability = 0;
        await h.HeardByAllFour();
        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(primary), "a recovered solve immediately displaces fallback");
        Assert.That(h.Reported, Is.EqualTo("Living Room"));
        Assert.That(nearest.WeightedConfidence, Is.Zero, "fallback cannot influence fusion while a position exists");

        primary.Probability = 1;
        nearest.Probability = 0;
        h.ExpireReadings();
        await h.Heard("node1", 2.83);
        await h.Locate();
        Assert.That(primary.Confidence, Is.Zero);
        Assert.That(primary.Room?.Name, Is.EqualTo("Living Room"));
        Assert.That(h.Device.BestScenario, Is.SameAs(nearest), "fallback immediately displaces a retained room");
        Assert.That(h.Reported, Is.EqualTo("Entry"));
        Assert.That(primary.WeightedConfidence, Is.Zero);

        await h.HeardByAllFour();
        await h.Locate();
        Assert.That(h.Device.BestScenario, Is.SameAs(primary));
        Assert.That(h.Reported, Is.EqualTo("Living Room"));

        h.ExpireReadings();
        await h.Locate();
        Assert.That(h.Reported, Is.EqualTo(DeviceReport.NotHome));
        Assert.That(h.Device.BestScenario, Is.Null, "neither locator can extend expired readings");
    }

    [Test]
    public async Task MultiFloor_UsesNearestNodeBelowFourReadings_AndSolvesAtFour()
    {
        await using var h = await Harness.StartAsync("multi_floor", nearestNode: true);
        h.Filtering.SmoothingWeight = 1;
        var count = 0;
        foreach (var (id, distance) in new[] { ("node1", 2.83), ("node2", 6.32), ("node3", 6.32) })
        {
            await h.Heard(id, distance);
            await h.Locate();
            var primary = h.Device.Scenarios.Single(s => !s.IsNearestNode);
            Assert.That(primary.Fixes, Is.EqualTo(++count));
            Assert.That(primary.Confidence, Is.Zero, $"{count} readings cannot resolve the 3D position");
            Assert.That(primary.Room, Is.Null, "no previous solve is available");
            Assert.That(h.Device.BestScenario!.IsNearestNode, Is.True);
            Assert.That(h.Reported, Is.EqualTo("Entry"));
        }

        await h.Heard("node4", 8.62);
        await h.Locate();
        var solved = h.Device.BestScenario!;
        Assert.That(solved.IsNearestNode, Is.False);
        Assert.That(solved.Fixes, Is.EqualTo(4));
        Assert.That(solved.Confidence, Is.GreaterThan(0));
        Assert.That(h.Reported, Is.EqualTo("Living Room"));
        var position = solved.Location;

        h.Device.Nodes["node4"].LastHit = DateTime.UtcNow.AddMinutes(-1);
        await h.Locate();
        Assert.That(solved.Fixes, Is.EqualTo(3));
        Assert.That(solved.Confidence, Is.Zero);
        Assert.That(solved.Location, Is.EqualTo(position), "dropping to three readings must not produce a new solve");
        Assert.That(h.Device.BestScenario!.IsNearestNode, Is.True);
        Assert.That(h.Reported, Is.EqualTo("Entry"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly YamlStateFixture _fx;
        private readonly DeviceTracker _tracker;
        private readonly DeviceStatePublisher _publisher;
        private readonly MultiScenarioLocator _locator;

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

        public string? Reported => _publisher.Reported(Device).State;

        public Task Locate() => _locator.ProcessDevice(Device);

        /// <summary>Distances from the four nodes to (3, 3, 1), inside Living Room.</summary>
        public async Task HeardByAllFour()
        {
            await Heard("node1", 2.83);
            await Heard("node2", 6.32);
            await Heard("node3", 6.32);
            await Heard("node4", 8.62);
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
