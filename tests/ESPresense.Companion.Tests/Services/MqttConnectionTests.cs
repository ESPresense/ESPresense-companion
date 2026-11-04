using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// The connection state machine against <see cref="FakeMqttClient"/>: connect on demand, one reconnect loop per
/// drop, config-change teardown, publish-time reconnect, and read-only mode.
/// </summary>
public class MqttConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class Harness
    {
        public ConfigMqtt Config = new() { Host = "broker.test", ClientId = "companion-test" };
        public readonly List<FakeMqttClient> Clients = new();
        public readonly ListLogger Logger = new();
        public Func<Task>? ConfigGate;
        public Action<FakeMqttClient>? Configure;
        public readonly MqttConnection Connection;

        public Harness(TimeSpan? reconnectDelay = null)
        {
            Connection = new MqttConnection(GetConfig, CreateClient, Logger) { ReconnectDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(20) };
        }

        private async Task<(ConfigMqtt?, ConfigMqtt)> GetConfig()
        {
            if (ConfigGate != null) await ConfigGate();
            var c = Config.Clone();
            return (c, c);
        }

        private FakeMqttClient CreateClient()
        {
            var client = new FakeMqttClient();
            Configure?.Invoke(client);
            lock (Clients) Clients.Add(client);
            return client;
        }

        public FakeMqttClient Client => Clients.Single();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("condition not met in time");
            await Task.Delay(5);
        }
    }

    [Test]
    public void NothingConnects_UntilFirstUse()
    {
        var h = new Harness();

        Assert.That(h.Clients, Is.Empty);
        Assert.That(h.Connection.DiscoveryTopic, Is.EqualTo("homeassistant"));
    }

    [Test]
    public async Task FirstPublish_ConnectsOnce_PublishesOnlineSubscribesAndSetsWill()
    {
        var h = new Harness();

        await h.Connection.PublishAsync("espresense/rooms/node-1/absorption/set", "3.00", retain: false);
        await h.Connection.PublishAsync("espresense/rooms/node-1/absorption/set", "3.10", retain: false);

        var client = h.Client;
        Assert.That(client.ConnectCalls, Is.EqualTo(1));
        Assert.That(client.Published, Is.EqualTo(new[]
        {
            new FakeMqttClient.Publish("espresense/companion/status", "online", true),
            new FakeMqttClient.Publish("espresense/rooms/node-1/absorption/set", "3.00", false),
            new FakeMqttClient.Publish("espresense/rooms/node-1/absorption/set", "3.10", false),
        }));
        Assert.That(client.Subscribed, Is.EqualTo(MqttTopics.Subscriptions("homeassistant")));
        Assert.That(client.Options.WillTopic, Is.EqualTo("espresense/companion/status"));
        Assert.That(client.Options.WillRetain, Is.True);
        Assert.That(System.Text.Encoding.UTF8.GetString(client.Options.WillPayload), Is.EqualTo("offline"));
        Assert.That(client.Options.ClientId, Is.EqualTo("companion-test"));
        Assert.DoesNotThrowAsync(() => h.Connection.WaitForConnectionAsync().WaitAsync(Timeout));
    }

    [Test]
    public async Task ConfiguredDiscoveryTopic_IsSubscribedAndExposedAfterConnect()
    {
        var h = new Harness();
        h.Config.DiscoveryTopic = "custom-ha";

        await h.Connection.GetClientAsync();

        Assert.That(h.Client.Subscribed, Does.Contain("custom-ha/device_tracker/+/config"));
        Assert.That(h.Connection.DiscoveryTopic, Is.EqualTo("custom-ha"));
    }

    [Test]
    public async Task ConcurrentFirstUse_BuildsOneClient()
    {
        var gate = new TaskCompletionSource();
        var h = new Harness { ConfigGate = () => gate.Task };

        var publishes = Enumerable.Range(0, 10).Select(i => h.Connection.PublishAsync($"t/{i}", "x", false)).ToArray();
        gate.SetResult();
        await Task.WhenAll(publishes).WaitAsync(Timeout);

        Assert.That(h.Clients, Has.Count.EqualTo(1));
        Assert.That(h.Client.ConnectCalls, Is.EqualTo(1));
    }

    [Test]
    public void FailedFirstConnect_Throws_AndNextUseBuildsAFreshClient()
    {
        var h = new Harness();
        h.Config = new ConfigMqtt { Host = "broker.test" };
        h.Configure = c => c.ConnectFailures = 1;

        Assert.ThrowsAsync<MQTTnet.Exceptions.MqttCommunicationException>(() => h.Connection.PublishAsync("t", "x", false));
        Assert.That(h.Clients[0].Disposed, Is.True, "a client whose first connect failed is disposed");

        h.Configure = null;
        Assert.DoesNotThrowAsync(() => h.Connection.PublishAsync("t", "x", false));
        Assert.That(h.Clients, Has.Count.EqualTo(2), "the next use builds a fresh client");
    }

    [Test]
    public async Task DisconnectWhileConnected_StartsExactlyOneReconnectLoop()
    {
        var h = new Harness();
        var client = (FakeMqttClient)await h.Connection.GetClientAsync();

        // Hold the reconnect attempt in flight so a second loop, if one were started, would show up.
        var release = new TaskCompletionSource();
        client.ConnectGate = () => release.Task;

        await client.DropAsync();
        await WaitUntil(() => client.ConnectCalls == 2);
        var loop = h.Connection.ReconnectTask;
        Assert.That(loop, Is.Not.Null);

        // Further disconnect notifications while the loop is running must not start another one.
        await client.DropAsync();
        await client.DropAsync();
        await Task.Delay(50);
        Assert.That(client.ConnectCalls, Is.EqualTo(2), "only one reconnect attempt may be in flight");
        Assert.That(h.Connection.ReconnectTask, Is.SameAs(loop));

        release.SetResult();
        await loop!.WaitAsync(Timeout);
        Assert.That(client.IsConnected, Is.True);
        Assert.That(h.Clients, Has.Count.EqualTo(1), "a reconnect reuses the client");
        Assert.That(client.Published.Count(p => p.Topic == "espresense/companion/status"), Is.EqualTo(2), "online is republished on reconnect");
    }

    [Test]
    public async Task ReconnectLoop_RetriesAfterFailures()
    {
        var h = new Harness();
        var client = (FakeMqttClient)await h.Connection.GetClientAsync();
        client.ConnectFailures = 2;

        await client.DropAsync();
        await WaitUntil(() => client.IsConnected);

        Assert.That(client.ConnectCalls, Is.EqualTo(4), "initial connect, two failures, then success");
    }

    [Test]
    public async Task ConfigChange_TearsDownOldClient_AndOldLoopExits()
    {
        var h = new Harness(reconnectDelay: TimeSpan.FromMilliseconds(20));
        var old = (FakeMqttClient)await h.Connection.GetClientAsync();

        // Old client drops and keeps failing to reconnect, so its loop is retrying.
        old.ConnectFailures = int.MaxValue;
        await old.DropAsync();
        await WaitUntil(() => old.ConnectCalls >= 2);
        var oldLoop = h.Connection.ReconnectTask;
        Assert.That(oldLoop, Is.Not.Null);

        h.Config = new ConfigMqtt { Host = "other-broker.test", ClientId = "companion-test" };
        h.Connection.OnConfigChanged(h.Config);
        Assert.That(h.Connection.ReconnectRequired, Is.True);

        await oldLoop!.WaitAsync(Timeout);

        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Clients, Has.Count.EqualTo(2));
        Assert.That(old.Disposed, Is.True);
        Assert.That(old.DisconnectCalls, Is.EqualTo(1));
        var current = h.Clients[1];
        Assert.That(current.IsConnected, Is.True);
        Assert.That(current.Published.Last(), Is.EqualTo(new FakeMqttClient.Publish("t", "x", false)));
        Assert.That(h.Connection.ReconnectRequired, Is.False);
        Assert.That(h.Connection.ReconnectTask, Is.Null, "no loop is started for the deliberate teardown");
    }

    [Test]
    public async Task DiscoveryTopicCaseChange_Reconnects_HostCaseChangeDoesNot()
    {
        var h = new Harness();
        h.Config.DiscoveryTopic = "HomeAssistant";
        await h.Connection.GetClientAsync();

        // DNS names are case-insensitive: not a change.
        var hostCase = h.Config.Clone();
        hostCase.Host = "BROKER.TEST";
        h.Connection.OnConfigChanged(hostCase);
        Assert.That(h.Connection.ReconnectRequired, Is.False);

        // MQTT topics are case-sensitive: a new discovery prefix.
        h.Config.DiscoveryTopic = "homeassistant";
        h.Connection.OnConfigChanged(h.Config);
        Assert.That(h.Connection.ReconnectRequired, Is.True);

        await h.Connection.PublishAsync("t", "x", false);
        Assert.That(h.Clients, Has.Count.EqualTo(2));
        Assert.That(h.Clients[1].Subscribed, Does.Contain("homeassistant/device_tracker/+/config"));
        Assert.That(h.Connection.DiscoveryTopic, Is.EqualTo("homeassistant"));
    }

    [Test]
    public async Task IrrelevantConfigChange_DoesNotReconnect()
    {
        var h = new Harness();
        await h.Connection.GetClientAsync();

        h.Connection.OnConfigChanged(h.Config.Clone());
        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Connection.ReconnectRequired, Is.False);
        Assert.That(h.Clients, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ConfigChangeDuringReconnect_IsNotLost()
    {
        var h = new Harness();
        await h.Connection.GetClientAsync();

        h.Config = new ConfigMqtt { Host = "second.test", ClientId = "companion-test" };
        h.Connection.OnConfigChanged(h.Config);

        // The reconnect for the first change takes its config snapshot, then a second change lands.
        var gate = new TaskCompletionSource();
        var snapshotTaken = new TaskCompletionSource();
        h.ConfigGate = () => { snapshotTaken.TrySetResult(); return gate.Task; };
        var publish = h.Connection.PublishAsync("t", "x", false);
        await snapshotTaken.Task.WaitAsync(Timeout);

        var third = new ConfigMqtt { Host = "third.test", ClientId = "companion-test" };
        h.Connection.OnConfigChanged(third);
        gate.SetResult();
        h.ConfigGate = null;
        await publish.WaitAsync(Timeout);

        // The reconnect used the config read before the second change, so another reconnect is still due.
        Assert.That(h.Connection.ReconnectRequired, Is.True);

        h.Config = third;
        await h.Connection.PublishAsync("t", "y", false);
        Assert.That(h.Clients, Has.Count.EqualTo(3));
        Assert.That(((MQTTnet.MqttClientTcpOptions)h.Clients[2].Options.ChannelOptions).RemoteEndpoint.ToString(), Does.Contain("third.test"));
        Assert.That(h.Connection.ReconnectRequired, Is.False);
    }

    [Test]
    public async Task ConfigChangeDuringFirstConnect_IsNotLost()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        var snapshotTaken = new TaskCompletionSource();
        h.ConfigGate = () => { snapshotTaken.TrySetResult(); return gate.Task; };

        // The first connect is in flight with the original settings when config.yaml is reloaded.
        var first = h.Connection.GetClientAsync();
        await snapshotTaken.Task.WaitAsync(Timeout);
        var reloaded = new ConfigMqtt { Host = "second.test", ClientId = "companion-test" };
        h.Connection.OnConfigChanged(reloaded);
        gate.SetResult();
        h.ConfigGate = null;
        await first.WaitAsync(Timeout);

        Assert.That(h.Connection.ReconnectRequired, Is.True, "the first connect used settings older than the reload");

        h.Config = reloaded;
        await h.Connection.PublishAsync("t", "x", false);
        Assert.That(h.Clients, Has.Count.EqualTo(2));
        Assert.That(((MQTTnet.MqttClientTcpOptions)h.Clients[1].Options.ChannelOptions).RemoteEndpoint.ToString(), Does.Contain("second.test"));
        Assert.That(h.Connection.ReconnectRequired, Is.False);
    }

    [Test]
    public async Task UnchangedReloadDuringFirstConnect_DoesNotReconnect()
    {
        // ConfigLoader raises ConfigChanged on every successful load, including the first one and saves of
        // unrelated sections; that must not bounce the connection.
        var h = new Harness();
        var gate = new TaskCompletionSource();
        var snapshotTaken = new TaskCompletionSource();
        h.ConfigGate = () => { snapshotTaken.TrySetResult(); return gate.Task; };

        var first = h.Connection.GetClientAsync();
        await snapshotTaken.Task.WaitAsync(Timeout);
        h.Connection.OnConfigChanged(h.Config.Clone());
        gate.SetResult();
        h.ConfigGate = null;
        await first.WaitAsync(Timeout);
        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Connection.ReconnectRequired, Is.False);
        Assert.That(h.Clients, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ReloadBeforeFirstConnect_DoesNotReconnect()
    {
        var h = new Harness();
        h.Config = new ConfigMqtt { Host = "second.test", ClientId = "companion-test" };
        h.Connection.OnConfigChanged(h.Config);

        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Connection.ReconnectRequired, Is.False, "the first connect already read the reloaded settings");
        Assert.That(h.Clients, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task PublishDuringDisconnect_ReconnectsOnce()
    {
        var h = new Harness();
        var client = (FakeMqttClient)await h.Connection.GetClientAsync();
        client.LoseConnectionSilently();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => h.Connection.PublishAsync($"t/{i}", "x", false))).WaitAsync(Timeout);

        Assert.That(client.ConnectCalls, Is.EqualTo(2), "one inline reconnect serves every waiting publisher");
        Assert.That(client.Published.Count(p => p.Topic.StartsWith("t/")), Is.EqualTo(5));
    }

    [Test]
    public async Task ReadOnlyFlag_NeverPublishes_NeverSetsWill_StillSubscribes()
    {
        var h = new Harness();
        h.Config.ReadOnly = true;

        await h.Connection.PublishAsync("espresense/rooms/node-1/absorption/set", "3.00", retain: true);

        Assert.That(h.Connection.IsReadOnly, Is.True);
        Assert.That(h.Client.Published, Is.Empty);
        Assert.That(h.Client.Options.WillTopic, Is.Null);
        Assert.That(h.Client.Subscribed, Is.EqualTo(MqttTopics.Subscriptions("homeassistant")));
        Assert.That(h.Logger.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Information && e.Message.Contains("ReadOnly, would have sent to espresense/rooms/node-1/absorption/set")));
        Assert.That(h.Logger.Entries, Has.None.Matches<(LogLevel Level, string Message)>(e => e.Level == LogLevel.Warning));
    }

    [Test]
    public async Task ReadOnlyHeuristic_StillWorks_ButWarnsOnStartup()
    {
        var h = new Harness();
        h.Config.ClientId = "bedroom-reader";

        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Connection.IsReadOnly, Is.True);
        Assert.That(h.Client.Published, Is.Empty);
        Assert.That(h.Client.Options.WillTopic, Is.Null);
        var warnings = h.Logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0].Message, Does.Contain("deprecated").And.Contain("read_only: true").And.Contain("bedroom-reader"));
    }

    [Test]
    public async Task ExplicitFlagFalse_WithNeutralClientId_Publishes()
    {
        var h = new Harness();
        h.Config.ClientId = "companion-main";

        await h.Connection.PublishAsync("t", "x", false);

        Assert.That(h.Connection.IsReadOnly, Is.False);
        Assert.That(h.Client.Published, Has.Some.EqualTo(new FakeMqttClient.Publish("t", "x", false)));
    }

    [Test]
    public async Task ReceivedMessages_AreForwarded()
    {
        var h = new Harness();
        var received = new List<string>();
        h.Connection.MessageReceived += e => { received.Add(e.ApplicationMessage.Topic); return Task.CompletedTask; };
        var client = (FakeMqttClient)await h.Connection.GetClientAsync();

        await client.DeliverAsync("espresense/devices/dev-1/node-1", "{}");

        Assert.That(received, Is.EqualTo(new[] { "espresense/devices/dev-1/node-1" }));
    }

    [Test]
    public async Task Dispose_StopsReconnectBackoff()
    {
        var h = new Harness(reconnectDelay: TimeSpan.FromHours(1));
        var client = (FakeMqttClient)await h.Connection.GetClientAsync();
        client.ConnectFailures = int.MaxValue;
        await client.DropAsync();
        await WaitUntil(() => client.ConnectCalls >= 2);
        var loop = h.Connection.ReconnectTask!;

        h.Connection.Dispose();

        await loop.WaitAsync(Timeout);
    }
}
