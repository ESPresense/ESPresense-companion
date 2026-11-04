using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests.Services;

/// <summary>The coordinator facade wired to a real <see cref="MqttConnection"/> over <see cref="FakeMqttClient"/>.</summary>
public class MqttCoordinatorFacadeTests
{
    private static (MqttCoordinator Coordinator, MqttConnection Connection, List<FakeMqttClient> Clients) Create(ConfigMqtt? config = null)
    {
        config ??= new ConfigMqtt { Host = "broker.test", ClientId = "companion-test" };
        var clients = new List<FakeMqttClient>();
        var connection = new MqttConnection(
            () => Task.FromResult<(ConfigMqtt?, ConfigMqtt)>((config.Clone(), config.Clone())),
            () => { var c = new FakeMqttClient(); clients.Add(c); return c; },
            null);
        var coordinator = new MqttCoordinator(connection) { ClearRetainedWindow = TimeSpan.FromMilliseconds(50) };
        return (coordinator, connection, clients);
    }

    [Test]
    public async Task InboundMessages_FlowFromTheClientToTheEvents()
    {
        var (coordinator, connection, _) = Create();
        var events = MqttRoutingCharacterizationTests.Record(coordinator);
        var client = (FakeMqttClient)await connection.GetClientAsync();

        await client.DeliverAsync("espresense/devices/dev-1/node-1", "{\"rssi\":-61}");
        await client.DeliverAsync("espresense/rooms/node-1/status", "online");
        await client.DeliverAsync("espresense/companion/lease/some-lease", "{}");

        Assert.That(events, Is.EqualTo(new[]
        {
            "DeviceMessageReceived device=dev-1 node=node-1 rssi=-61",
            "NodeStatusReceived node=node-1 online=True",
            "MqttMessageReceived topic=espresense/companion/lease/some-lease",
        }));
    }

    [Test]
    public async Task DiscoveryPrefix_FollowsTheConnection()
    {
        var (coordinator, connection, _) = Create(new ConfigMqtt { Host = "broker.test", ClientId = "companion-test", DiscoveryTopic = "custom-ha" });
        var events = MqttRoutingCharacterizationTests.Record(coordinator);
        var client = (FakeMqttClient)await connection.GetClientAsync();

        await client.DeliverAsync("custom-ha/device_tracker/espresense-dev-1/config", "");
        await client.DeliverAsync("homeassistant/device_tracker/espresense-dev-1/config", "");

        Assert.That(coordinator.DiscoveryTopic, Is.EqualTo("custom-ha"));
        Assert.That(events, Is.EqualTo(new[]
        {
            "PreviousDeviceDiscovered id=espresense-dev-1 device=<null> auto=False",
            "MqttMessageReceived topic=homeassistant/device_tracker/espresense-dev-1/config",
        }));
    }

    [Test]
    public async Task EnqueueAsync_PublishesThroughTheConnection()
    {
        var (coordinator, _, clients) = Create();

        await coordinator.EnqueueAsync("espresense/settings/dev-1/config", "{}", retain: true);
        Assert.That(await coordinator.TryEnqueueAsync("espresense/companion/telemetry", "{}"), Is.True);

        Assert.That(clients.Single().Published, Is.EqualTo(new[]
        {
            new FakeMqttClient.Publish("espresense/companion/status", "online", true),
            new FakeMqttClient.Publish("espresense/settings/dev-1/config", "{}", true),
            new FakeMqttClient.Publish("espresense/companion/telemetry", "{}", false),
        }));
    }

    [Test]
    public async Task ClearRetained_ClearsOnlyTopicsMatchingTheFilter()
    {
        var (coordinator, connection, _) = Create();
        var events = MqttRoutingCharacterizationTests.Record(coordinator);
        var client = (FakeMqttClient)await connection.GetClientAsync();

        client.OnSubscribed = async filter =>
        {
            if (filter != "espresense/rooms/node-1/#") return;
            // Retained state of the node being removed...
            await client.DeliverAsync("espresense/rooms/node-1/telemetry", "{\"ip\":\"10.0.0.1\"}", retain: true);
            await client.DeliverAsync("espresense/rooms/node-1/absorption", "3.50", retain: true);
            // ...and unrelated live traffic that happens to arrive in the same window.
            await client.DeliverAsync("espresense/devices/dev-1/node-2", "{\"rssi\":-70}");
            await client.DeliverAsync("espresense/rooms/node-2/status", "online");
        };

        await coordinator.ClearRetainedAsync("espresense/rooms/node-1/#");

        var clears = client.Published.Where(p => p.Payload == null).ToList();
        Assert.That(clears, Is.EquivalentTo(new[]
        {
            new FakeMqttClient.Publish("espresense/rooms/node-1/telemetry", null, true),
            new FakeMqttClient.Publish("espresense/rooms/node-1/absorption", null, true),
        }));
        Assert.That(client.Unsubscribed, Is.EqualTo(new[] { "espresense/rooms/node-1/#" }));

        // Documented behaviour: the retained replay also goes through normal routing.
        Assert.That(events, Does.Contain("NodeTelemetryReceived node=node-1 ip=10.0.0.1"));
    }

    [Test]
    public async Task ClearRetained_InReadOnlyMode_PublishesNothing()
    {
        var (coordinator, connection, _) = Create(new ConfigMqtt { Host = "broker.test", ClientId = "companion-test", ReadOnly = true });
        var client = (FakeMqttClient)await connection.GetClientAsync();
        client.OnSubscribed = filter => filter == "espresense/rooms/node-1/#"
            ? client.DeliverAsync("espresense/rooms/node-1/telemetry", "{}", retain: true)
            : Task.CompletedTask;

        await coordinator.ClearRetainedAsync("espresense/rooms/node-1/#");

        Assert.That(client.Published, Is.Empty);
    }
}
