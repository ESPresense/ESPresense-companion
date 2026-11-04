using ESPresense.Controllers;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SQLite;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Pins what <see cref="MultiScenarioLocator"/> puts on the wire. The state topic
/// (espresense/companion/{id}) and the attributes JSON (espresense/companion/{id}/attributes)
/// feed Home Assistant's device_tracker, so they are a public contract: these strings
/// must not change when the locator is refactored.
/// </summary>
public class DevicePublishContractTests
{
    private static readonly DateTime FixedSeen = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private sealed class FixedLocator(Point3D at, int confidence, int fixes, Floor? floor, Room? room, bool moved = true) : ILocate
    {
        public bool Locate(Scenario scenario)
        {
            scenario.ResetLocation(at);
            scenario.LastHit = FixedSeen;
            scenario.Confidence = confidence;
            scenario.Fixes = fixes;
            scenario.Floor = floor;
            scenario.Room = room;
            return moved;
        }
    }

    private sealed class Harness
    {
        public readonly Mock<MqttCoordinator> Mqtt;
        public readonly State State;
        public readonly MultiScenarioLocator Locator;
        public readonly List<(string Topic, string? Payload)> Sent = new();
        public readonly Config Config = new() { Timeout = int.MaxValue };
        public readonly Floor Floor = new();
        public readonly Room Room = new();

        public Harness(string name, bool gps = false)
        {
            var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-contract-" + name);
            Directory.CreateDirectory(workDir);
            var configLoader = new ConfigLoader(workDir);
            var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
            Mqtt = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
            Mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), true))
                .Callback<string, string?, bool>((t, p, _) => Sent.Add((t, p)))
                .ReturnsAsync(true);

            State = new State(configLoader, new NodeTelemetryStore(Mqtt.Object));
            if (gps) Config.Gps = new ConfigGps { Latitude = 40.0, Longitude = -75.0, Elevation = 100.0, Rotation = 30, Report = true };
            State.Apply(Config);

            var tele = new TelemetryService(Mqtt.Object);
            var dss = new DeviceSettingsStore(Mqtt.Object, State);
            var tracker = new DeviceTracker(State, Mqtt.Object, tele, new GlobalEventDispatcher(), dss);
            var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
            Locator = new MultiScenarioLocator(tracker, State, new DeviceStatePublisher(Mqtt.Object), new DeviceHistoryWriter(history, State), new GlobalEventDispatcher(), new Mock<ILeaseService>().Object, NullLogger<MultiScenarioLocator>.Instance);

            Floor.Update(Config, new ConfigFloor { Id = "f1", Name = "Floor One", Bounds = [[0, 0, 0], [10, 10, 3]] });
            Room.Update(Config, Floor, new ConfigRoom { Id = "r1", Name = "Room One", Points = [[0, 0], [5, 0], [5, 5], [0, 5]] });
        }

        public Device Located(Point3D at, Floor? floor, Room? room, int confidence = 87, int fixes = 4)
        {
            var device = new Device("test:located", null, TimeSpan.FromDays(3650));
            device.Scenarios.Add(new Scenario(Config, new FixedLocator(at, confidence, fixes, floor, room), "Floor One"));
            return device;
        }

        public Device Anchored(Point3D at, Floor? floor, Room? room, bool heard)
        {
            var device = new Device("test:anchored", null, TimeSpan.FromDays(3650)) { LastSeen = FixedSeen };
            device.SetAnchor(new DeviceAnchor(at, floor, room));
            if (heard)
                device.Nodes["n1"] = new DeviceToNode(device, new Node("n1", NodeSourceType.Config)) { LastHit = FixedSeen };
            return device;
        }

        public string?[] On(string topic) => Sent.Where(s => s.Topic == topic).Select(s => s.Payload).ToArray();
    }

    [Test]
    public async Task Located_NoGps_AttributesJson()
    {
        var h = new Harness("located-nogps");
        var device = h.Located(new Point3D(1.5, 2.25, 0.75), h.Floor, h.Room);

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:located"), Is.EqualTo(new[] { "Room One" }));
        Assert.That(h.On("espresense/companion/test:located/attributes"), Is.EqualTo(new[]
        {
            "{\"source_type\":\"espresense\",\"x\":1.5,\"y\":2.25,\"z\":0.75,\"confidence\":87,\"fixes\":4,\"best_scenario\":\"Floor One\",\"last_seen\":\"2026-01-02T03:04:05Z\"}"
        }));
    }

    [Test]
    public async Task Located_WithGps_AttributesJson()
    {
        var h = new Harness("located-gps", gps: true);
        var device = h.Located(new Point3D(1.5, 2.25, 0.75), h.Floor, h.Room);

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:located/attributes"), Is.EqualTo(new[]
        {
            "{\"source_type\":\"espresense\",\"latitude\":40.00001076682214,\"longitude\":-74.99997157409732,\"elevation\":100.75,\"x\":1.5,\"y\":2.25,\"z\":0.75,\"confidence\":87,\"fixes\":4,\"best_scenario\":\"Floor One\",\"last_seen\":\"2026-01-02T03:04:05Z\"}"
        }));
    }

    [Test]
    public async Task Located_ZeroValuesAreOmitted()
    {
        // The MQTT JSON options (formerly Newtonsoft NullIgnore) also drop defaults: a zero coordinate disappears, but a located zero confidence/fixes (int?) stays.
        // (The anchored payload's fixes = 0 is a plain int, so it is dropped; see Anchored_NoGps_AttributesJson.)
        var h = new Harness("located-zero");
        var device = h.Located(new Point3D(0, 3, 0), h.Floor, null, confidence: 0, fixes: 0);

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:located"), Is.EqualTo(new[] { "Floor One" }));
        Assert.That(h.On("espresense/companion/test:located/attributes"), Is.EqualTo(new[]
        {
            "{\"source_type\":\"espresense\",\"y\":3.0,\"confidence\":0,\"fixes\":0,\"best_scenario\":\"Floor One\",\"last_seen\":\"2026-01-02T03:04:05Z\"}"
        }));
    }

    [Test]
    public async Task Located_StateIsRoomThenFloorThenNotHome()
    {
        var h = new Harness("located-state");

        await h.Locator.ProcessDevice(h.Located(new Point3D(1, 1, 1), h.Floor, h.Room));
        await h.Locator.ProcessDevice(h.Located(new Point3D(1, 1, 1), h.Floor, null));
        await h.Locator.ProcessDevice(h.Located(new Point3D(1, 1, 1), null, null));

        Assert.That(h.On("espresense/companion/test:located"), Is.EqualTo(new[] { "Room One", "Floor One", "not_home" }));
    }

    [Test]
    public async Task Located_NoCurrentScenario_PublishesNotHomeOnlyOnce_AndNoAttributes()
    {
        var h = new Harness("located-gone");
        var device = h.Located(new Point3D(1, 1, 1), h.Floor, h.Room);
        await h.Locator.ProcessDevice(device);
        h.Sent.Clear();

        h.Config.Timeout = 1; // the scenario's last hit is now stale
        await h.Locator.ProcessDevice(device);
        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:located"), Is.EqualTo(new[] { "not_home" }));
        Assert.That(h.On("espresense/companion/test:located/attributes"), Is.Empty);
    }

    [Test]
    public async Task Located_NothingMovedAndStateUnchanged_PublishesNothing()
    {
        var h = new Harness("located-still");
        var config = h.Config;
        var device = new Device("test:located", null, TimeSpan.FromDays(3650));
        var locator = new FixedLocator(new Point3D(1, 1, 1), 50, 3, h.Floor, h.Room, moved: false);
        device.Scenarios.Add(new Scenario(config, locator, "Floor One"));

        await h.Locator.ProcessDevice(device);
        // First pass: state changed (unset -> Room One), so state + attributes go out
        Assert.That(h.Sent, Has.Count.EqualTo(2));
        h.Sent.Clear();

        await h.Locator.ProcessDevice(device);
        Assert.That(h.Sent, Is.Empty);
    }

    [Test]
    public async Task Located_MovedWithSameState_RepublishesAttributesOnly()
    {
        var h = new Harness("located-moved");
        var device = h.Located(new Point3D(1, 1, 1), h.Floor, h.Room);
        await h.Locator.ProcessDevice(device);
        h.Sent.Clear();

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:located"), Is.Empty);
        Assert.That(h.On("espresense/companion/test:located/attributes"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Anchored_NoGps_AttributesJson()
    {
        var h = new Harness("anchored-nogps");
        var device = h.Anchored(new Point3D(2.5, 3.5, 1.25), h.Floor, h.Room, heard: true);

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:anchored"), Is.EqualTo(new[] { "Room One" }));
        Assert.That(h.On("espresense/companion/test:anchored/attributes"), Is.EqualTo(new[]
        {
            "{\"source_type\":\"espresense\",\"x\":2.5,\"y\":3.5,\"z\":1.25,\"confidence\":100,\"best_scenario\":\"Anchored\",\"last_seen\":\"2026-01-02T03:04:05Z\"}"
        }));
    }

    [Test]
    public async Task Anchored_WithGps_AttributesJson()
    {
        var h = new Harness("anchored-gps", gps: true);
        var device = h.Anchored(new Point3D(2.5, 3.5, 1.25), h.Floor, h.Room, heard: true);

        await h.Locator.ProcessDevice(device);

        Assert.That(h.On("espresense/companion/test:anchored/attributes"), Is.EqualTo(new[]
        {
            "{\"source_type\":\"espresense\",\"latitude\":40.00001599979393,\"longitude\":-74.99995408932966,\"elevation\":101.25,\"x\":2.5,\"y\":3.5,\"z\":1.25,\"confidence\":100,\"best_scenario\":\"Anchored\",\"last_seen\":\"2026-01-02T03:04:05Z\"}"
        }));
    }

    [Test]
    public async Task Anchored_StateIsRoomThenFloorThenNotHome()
    {
        var h = new Harness("anchored-state");

        await h.Locator.ProcessDevice(h.Anchored(new Point3D(1, 1, 1), h.Floor, h.Room, heard: true));
        await h.Locator.ProcessDevice(h.Anchored(new Point3D(1, 1, 1), h.Floor, null, heard: true));
        await h.Locator.ProcessDevice(h.Anchored(new Point3D(1, 1, 1), null, null, heard: true));
        await h.Locator.ProcessDevice(h.Anchored(new Point3D(1, 1, 1), h.Floor, h.Room, heard: false));

        Assert.That(h.On("espresense/companion/test:anchored"), Is.EqualTo(new[] { "Room One", "Floor One", "not_home", "not_home" }));
    }

    [Test]
    public async Task Anchored_Unchanged_PublishesNothing()
    {
        var h = new Harness("anchored-still");
        var device = h.Anchored(new Point3D(1, 1, 1), h.Floor, h.Room, heard: true);
        await h.Locator.ProcessDevice(device);
        h.Sent.Clear();

        await h.Locator.ProcessDevice(device);

        Assert.That(h.Sent, Is.Empty);
    }
}

public class MultiScenarioLocatorLeaseTests
{
    [Test]
    public async Task StopsLocatingWhenTheLeaseIsLost()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-lease-loss");
        Directory.CreateDirectory(workDir);
        var configLoader = new ConfigLoader(workDir);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
        var published = new List<string>();
        mqtt.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), true))
            .Callback<string, string?, bool>((t, _, _) => { lock (published) published.Add(t); })
            .ReturnsAsync(true);

        var state = new State(configLoader, new NodeTelemetryStore(mqtt.Object));
        var events = new GlobalEventDispatcher();
        var tracker = new DeviceTracker(state, mqtt.Object, new TelemetryService(mqtt.Object), events, new DeviceSettingsStore(mqtt.Object, state));
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);

        var hasLease = true;
        var acquired = 0;
        var lease = new Mock<ILeaseService>();
        lease.Setup(l => l.HasLease("locating")).Returns(() => hasLease);
        lease.Setup(l => l.AcquireAsync("locating", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns<string, TimeSpan?, CancellationToken>(async (_, _, ct) =>
            {
                if (Interlocked.Increment(ref acquired) == 1) return new LeaseHandle(lease.Object, "locating", NullLogger.Instance);
                await Task.Delay(Timeout.Infinite, ct); // someone else holds it now
                return null;
            });

        var locator = new MultiScenarioLocator(tracker, state, new DeviceStatePublisher(mqtt.Object), new DeviceHistoryWriter(history, state), events, lease.Object, NullLogger<MultiScenarioLocator>.Instance);

        var first = new Device("test:first", null, TimeSpan.FromSeconds(30));
        var second = new Device("test:second", null, TimeSpan.FromSeconds(30));
        state.Devices[first.Id] = first;
        state.Devices[second.Id] = second;

        await locator.StartAsync(CancellationToken.None);
        try
        {
            tracker.MarkForLocate(first.Id);
            await WaitUntil(() => first.LastCalculated != null);

            hasLease = false;
            tracker.MarkForLocate(second.Id);
            await WaitUntil(() => state.LocatorState.Status == "Acquiring lease..." && Volatile.Read(ref acquired) >= 2);

            Assert.That(second.LastCalculated, Is.Null, "a device after the lease was lost must not be located");
            lease.Verify(l => l.ReleaseAsync("locating"), Times.Once);
        }
        finally
        {
            await locator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting for the locator loop");
            await Task.Delay(10);
        }
    }
}
