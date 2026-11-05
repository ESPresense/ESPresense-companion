using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests.Services;

/// <summary>The per-family handlers, tested on their own against a fake event sink (no coordinator, no broker).</summary>
public class MqttMessageHandlerTests
{
    private sealed class FakeSink : IMqttEventSink
    {
        public Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceived { get; set; }
        public Func<DeviceMessageEventArgs, Task>? DeviceMessageReceived { get; set; }
        public Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceived { get; set; }
        public Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceived { get; set; }
        public Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemoved { get; set; }
        public Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceived { get; set; }
        public Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemoved { get; set; }
        public Func<NodeUpdateErrorEventArgs, Task>? NodeUpdateError { get; set; }
        public Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceived { get; set; }
        public List<PreviousDeviceDiscoveredEventArgs> Discovered { get; } = new();
        public void RaisePreviousDeviceDiscovered(PreviousDeviceDiscoveredEventArgs args) => Discovered.Add(args);
    }

    [Test]
    public async Task Telemetry_ParsesPayload()
    {
        var sink = new FakeSink();
        NodeTelemetryReceivedEventArgs? got = null;
        sink.NodeTelemetryReceived = a => { got = a; return Task.CompletedTask; };

        await new NodeTelemetryHandler(sink).HandleAsync(new NodeTelemetryRoute("espresense/rooms/n/telemetry", "n"), "{\"ip\":\"10.0.0.9\"}");

        Assert.That(got!.NodeId, Is.EqualTo("n"));
        Assert.That(got.Payload.Ip, Is.EqualTo("10.0.0.9"));
    }

    [Test]
    public void Telemetry_InvalidJson_ThrowsProcessingExceptionWithReceivedTopic()
    {
        var sink = new FakeSink { NodeTelemetryReceived = _ => Task.CompletedTask };

        var ex = Assert.ThrowsAsync<MqttMessageProcessingException>(() =>
            new NodeTelemetryHandler(sink).HandleAsync(new NodeTelemetryRoute("espresense/rooms/n/telemetry", "n"), "{bad"));

        Assert.That(ex!.Topic, Is.EqualTo("espresense/rooms/n/telemetry"));
        Assert.That(ex.MessageType, Is.EqualTo("Telemetry"));
        Assert.That(ex.Payload, Is.EqualTo("{bad"));
        Assert.That(ex.Message, Is.EqualTo("Failed to parse telemetry data"));
    }

    [Test]
    public void Telemetry_InvalidJson_WithoutSubscribers_IsNotParsed()
    {
        var sink = new FakeSink();

        Assert.DoesNotThrowAsync(() =>
            new NodeTelemetryHandler(sink).HandleAsync(new NodeTelemetryRoute("espresense/rooms/n/telemetry", "n"), "{bad"));
    }

    [Test]
    public void DeviceMessage_NullJson_ThrowsNullMessage()
    {
        var sink = new FakeSink { DeviceMessageReceived = _ => Task.CompletedTask };

        var ex = Assert.ThrowsAsync<MqttMessageProcessingException>(() =>
            new DeviceMessageHandler(sink).HandleAsync(new DeviceMessageRoute("espresense/devices/d/n", "d", "n"), "null"));

        Assert.That(ex!.Message, Is.EqualTo("Device message was null after deserialization"));
        Assert.That(ex.MessageType, Is.EqualTo("DeviceMessage"));
    }

    [Test]
    public void DeviceSettings_IsParsedEvenWithoutSubscribers()
    {
        var sink = new FakeSink();

        var ex = Assert.ThrowsAsync<MqttMessageProcessingException>(() =>
            new DeviceSettingsHandler(sink).HandleAsync(new DeviceSettingsRoute("espresense/settings/d/config", "d"), "{bad"));

        Assert.That(ex!.MessageType, Is.EqualTo("DeviceConfig"));
    }

    [Test]
    public async Task DeviceSettings_SetsOriginalIdFromTopic()
    {
        var sink = new FakeSink();
        DeviceSettingsEventArgs? got = null;
        sink.DeviceConfigReceived = a => { got = a; return Task.CompletedTask; };

        await new DeviceSettingsHandler(sink).HandleAsync(new DeviceSettingsRoute("espresense/settings/d/config", "d"), "{\"id\":\"alias\",\"name\":\"Beacon\"}");

        Assert.That(got!.DeviceId, Is.EqualTo("d"));
        Assert.That(got.Payload.OriginalId, Is.EqualTo("d"));
        Assert.That(got.Payload.Name, Is.EqualTo("Beacon"));
    }

    [Test]
    public void DeviceAttributes_NonObject_ThrowsProcessingException()
    {
        var sink = new FakeSink { DeviceAttributesReceived = _ => Task.CompletedTask };

        var ex = Assert.ThrowsAsync<MqttMessageProcessingException>(() =>
            new DeviceAttributesHandler(sink).HandleAsync(new DeviceAttributesRoute("espresense/companion/d/attributes", "d"), "[1]"));

        Assert.That(ex!.Topic, Is.EqualTo("espresense/companion/d/attributes"));
        Assert.That(ex.MessageType, Is.EqualTo("DeviceAttributes"));
    }

    [TestCase("online", true)]
    [TestCase("offline", false)]
    [TestCase("Online", false)]
    public async Task Status_OnlineOnlyForExactOnline(string payload, bool online)
    {
        var sink = new FakeSink();
        NodeStatusReceivedEventArgs? got = null;
        sink.NodeStatusReceived = a => { got = a; return Task.CompletedTask; };

        await new NodeStatusHandler(sink).HandleAsync(new NodeStatusRoute("espresense/rooms/n/status", "n"), payload);

        Assert.That(got!.Online, Is.EqualTo(online));
    }

    [Test]
    public async Task Discovery_ClearedPayload_RaisesWithNullDevice()
    {
        var sink = new FakeSink();

        await new DiscoveryHandler(sink, () => "homeassistant", null).HandleAsync(new DiscoveryRoute("homeassistant/device_tracker/espresense-d/config", "espresense-d"), "");

        Assert.That(sink.Discovered, Has.Count.EqualTo(1));
        Assert.That(sink.Discovered[0].DiscoveryId, Is.EqualTo("espresense-d"));
        Assert.That(sink.Discovered[0].DeviceId, Is.Null);
        Assert.That(sink.Discovered[0].AutoDiscover, Is.Null);
    }

    [Test]
    public void Discovery_MalformedPayload_LogsUnderAutoDiscovery_NotTheCoordinator()
    {
        var sink = new FakeSink();
        var coordinatorLogger = new Microsoft.Extensions.Logging.Testing.FakeLogger<MqttCoordinator>();
        var discoveryLogger = new Microsoft.Extensions.Logging.Testing.FakeLogger<AutoDiscovery>();
        var handler = new DiscoveryHandler(sink, () => "homeassistant", coordinatorLogger, discoveryLogger);

        Assert.ThrowsAsync<MqttMessageProcessingException>(() =>
            handler.HandleAsync(new DiscoveryRoute("homeassistant/device_tracker/espresense-d/config", "espresense-d"), "{not json"));

        var parseFailure = discoveryLogger.Collector.GetSnapshot().Single(r => r.Message.StartsWith("Failed to deserialize discovery message"));
        Assert.That(parseFailure.Category, Is.EqualTo(typeof(AutoDiscovery).FullName));
        Assert.That(coordinatorLogger.Collector.GetSnapshot().Where(r => r.Message.StartsWith("Failed to deserialize")), Is.Empty);
    }

    [Test]
    public async Task Dispatcher_SendsEachRouteToItsHandler()
    {
        var sink = new FakeSink();
        var seen = new List<string>();
        sink.NodeSettingReceived = a => { seen.Add($"setting {a.NodeId}/{a.Setting}={a.Payload}"); return Task.CompletedTask; };
        sink.NodeUpdateError = a => { seen.Add($"update_error {a.NodeId}={a.Error ?? "<null>"}"); return Task.CompletedTask; };
        sink.NodeStatusRemoved = a => { seen.Add($"status removed {a.NodeId}"); return Task.CompletedTask; };
        sink.NodeTelemetryRemoved = a => { seen.Add($"telemetry removed {a.NodeId}"); return Task.CompletedTask; };
        var handlers = new MqttMessageHandlers(sink, () => "homeassistant", null);

        await handlers.DispatchAsync(new NodeSettingRoute("t", "*", "absorption"), "2.00");
        await handlers.DispatchAsync(new NodeUpdateErrorRoute("t", "n"), "");
        await handlers.DispatchAsync(new NodeStatusRoute("t", "n"), null);
        await handlers.DispatchAsync(new NodeTelemetryRoute("t", "n"), "");

        Assert.That(seen, Is.EqualTo(new[] { "setting */absorption=2.00", "update_error n=<null>", "status removed n", "telemetry removed n" }));
    }
}
