using System.Text;
using ESPresense.Services;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Packets;
using Moq;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Characterizes the inbound routing of <see cref="MqttCoordinator"/> end to end: for every topic shape the
/// coordinator subscribes to, which public event fires and with which arguments. Written against the
/// pre-split coordinator (#1843) so the split into router + handlers can be checked against it.
/// </summary>
public class MqttRoutingCharacterizationTests
{
    private const string DiscoveryPayload =
        "{\"name\":\"Phone\",\"unique_id\":\"espresense-companion-phone-1\",\"state_topic\":\"espresense/companion/phone-1\",\"origin\":{\"name\":\"ESPresense Companion\"}}";

    private const string ForeignDiscoveryPayload =
        "{\"name\":\"Other\",\"state_topic\":\"x/y\",\"origin\":{\"name\":\"Something Else\"}}";

    public static IEnumerable<TestCaseData> Cases()
    {
        TestCaseData Case(string topic, string? payload, params string[] expected) =>
            new TestCaseData(topic, payload, expected).SetName($"{{m}}({topic} <- {payload ?? "null"})");

        // Node telemetry
        yield return Case("espresense/rooms/node-1/telemetry", "{\"ip\":\"10.0.0.1\",\"uptime\":10}", "NodeTelemetryReceived node=node-1 ip=10.0.0.1");
        yield return Case("espresense/rooms/node-1/telemetry", null, "NodeTelemetryRemoved node=node-1");
        yield return Case("espresense/rooms/node-1/telemetry", "", "NodeTelemetryRemoved node=node-1");
        yield return Case("espresense/rooms/node-1/telemetry", "not json", "Malformed");
        yield return Case("espresense/rooms/node-1/telemetry", "null", "Malformed");

        // Node status
        yield return Case("espresense/rooms/node-1/status", "online", "NodeStatusReceived node=node-1 online=True");
        yield return Case("espresense/rooms/node-1/status", "offline", "NodeStatusReceived node=node-1 online=False");
        yield return Case("espresense/rooms/node-1/status", "ONLINE", "NodeStatusReceived node=node-1 online=False");
        yield return Case("espresense/rooms/node-1/status", null, "NodeStatusRemoved node=node-1");

        // Node update_error
        yield return Case("espresense/rooms/node-1/update_error", "http 404", "NodeUpdateError node=node-1 error=http 404");
        yield return Case("espresense/rooms/node-1/update_error", null, "NodeUpdateError node=node-1 error=<null>");

        // Node settings: espresense/rooms/{node}/{setting} and the broadcast espresense/rooms/*/{setting}/set
        yield return Case("espresense/rooms/node-1/absorption", "3.50", "NodeSettingReceived node=node-1 setting=absorption payload=3.50");
        yield return Case("espresense/rooms/node-1/absorption", null, "NodeSettingReceived node=node-1 setting=absorption payload=<null>");
        yield return Case("espresense/rooms/*/absorption/set", "2.00", "NodeSettingReceived node=* setting=absorption payload=2.00");
        yield return Case("espresense/rooms/*/telemetry", "{\"ip\":\"10.0.0.2\"}", "NodeTelemetryReceived node=* ip=10.0.0.2");

        // Device messages
        yield return Case("espresense/devices/dev-1/node-1", "{\"rssi\":-70}", "DeviceMessageReceived device=dev-1 node=node-1 rssi=-70");
        yield return Case("espresense/devices/dev-1/node-1", null);
        yield return Case("espresense/devices/dev-1/node-1", "");
        yield return Case("espresense/devices/dev-1/node-1", "{bad", "Malformed");
        yield return Case("espresense/devices/dev-1/node-1", "null", "Malformed");

        // Device settings
        yield return Case("espresense/settings/dev-1/config", "{\"name\":\"Beacon\"}", "DeviceConfigReceived device=dev-1 original=dev-1 name=Beacon");
        yield return Case("espresense/settings/dev-1/config", "{bad", "Malformed");
        yield return Case("espresense/settings/dev-1/config", null, "Malformed");

        // Companion device attributes
        yield return Case("espresense/companion/dev-1/attributes", "{\"room\":\"r1\"}", "DeviceAttributesReceived device=dev-1 keys=room");
        yield return Case("espresense/companion/dev-1/attributes", null);
        yield return Case("espresense/companion/dev-1/attributes", "[1]", "Malformed");

        // Home Assistant discovery (default discovery prefix before any connect is "homeassistant")
        yield return Case("homeassistant/device_tracker/espresense-phone-1/config", DiscoveryPayload, "PreviousDeviceDiscovered id=espresense-phone-1 device=phone-1 auto=True");
        yield return Case("homeassistant/device_tracker/espresense-phone-1/config", null, "PreviousDeviceDiscovered id=espresense-phone-1 device=<null> auto=False");
        yield return Case("homeassistant/device_tracker/espresense-phone-1/config", "  ", "PreviousDeviceDiscovered id=espresense-phone-1 device=<null> auto=False");
        yield return Case("homeassistant/device_tracker/espresense-phone-1/config", ForeignDiscoveryPayload, "Malformed");

        // Everything else falls through to MqttMessageReceivedAsync
        yield return Case("espresense/companion/lease/some-lease", "{}", "MqttMessageReceived topic=espresense/companion/lease/some-lease");
        yield return Case("espresense/companion/status", "online", "MqttMessageReceived topic=espresense/companion/status");
        yield return Case("espresense/rooms/node-1/absorption/set", "1", "MqttMessageReceived topic=espresense/rooms/node-1/absorption/set");
        yield return Case("espresense/settings/dev-1/other", "{}", "MqttMessageReceived topic=espresense/settings/dev-1/other");
        yield return Case("espresense/devices/dev-1", "{}", "MqttMessageReceived topic=espresense/devices/dev-1");
        yield return Case("homeassistant/sensor/x/config", "{}", "MqttMessageReceived topic=homeassistant/sensor/x/config");
        yield return Case("other/device_tracker/x/config", "{}", "MqttMessageReceived topic=other/device_tracker/x/config");
        yield return Case("some/other/topic", "payload", "MqttMessageReceived topic=some/other/topic");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Topic_RaisesExpectedEvent(string topic, string? payload, string[] expected)
    {
        var coordinator = CreateCoordinator();
        var events = Record(coordinator);

        await coordinator.OnMqttMessageReceived(Message(topic, payload));

        Assert.That(events, Is.EqualTo(expected));
    }

    internal static MqttCoordinator CreateCoordinator()
    {
        var configLoader = new Mock<ConfigLoader>("test-config-dir");
        return new MqttCoordinator(
            configLoader.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<MqttCoordinator>>(),
            Mock.Of<IMqttNetLogger>(),
            new SupervisorConfigLoader(Mock.Of<Microsoft.Extensions.Logging.ILogger<SupervisorConfigLoader>>()));
    }

    internal static MqttApplicationMessageReceivedEventArgs Message(string topic, string? payload)
    {
        var message = new MqttApplicationMessage { Topic = topic };
        if (payload != null)
            message.PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload));
        return new MqttApplicationMessageReceivedEventArgs("test-client", message, new MqttPublishPacket(), null!);
    }

    private static string N(string? s) => s ?? "<null>";

    /// <summary>Subscribes to every public event and records each raise as a one-line summary.</summary>
    internal static List<string> Record(IMqttCoordinator c)
    {
        var events = new List<string>();
        c.NodeTelemetryReceivedAsync += a => Add($"NodeTelemetryReceived node={a.NodeId} ip={a.Payload.Ip}");
        c.NodeTelemetryRemovedAsync += a => Add($"NodeTelemetryRemoved node={a.NodeId}");
        c.NodeStatusReceivedAsync += a => Add($"NodeStatusReceived node={a.NodeId} online={a.Online}");
        c.NodeStatusRemovedAsync += a => Add($"NodeStatusRemoved node={a.NodeId}");
        c.NodeUpdateErrorAsync += a => Add($"NodeUpdateError node={a.NodeId} error={N(a.Error)}");
        c.NodeSettingReceivedAsync += a => Add($"NodeSettingReceived node={a.NodeId} setting={a.Setting} payload={N(a.Payload)}");
        c.DeviceMessageReceivedAsync += a => Add($"DeviceMessageReceived device={a.DeviceId} node={a.NodeId} rssi={a.Payload.Rssi}");
        c.DeviceConfigReceivedAsync += a => Add($"DeviceConfigReceived device={a.DeviceId} original={a.Payload.OriginalId} name={a.Payload.Name}");
        c.DeviceAttributesReceivedAsync += a => Add($"DeviceAttributesReceived device={a.DeviceId} keys={string.Join(",", a.Attributes.Keys)}");
        c.MqttMessageReceivedAsync += a => Add($"MqttMessageReceived topic={a.ApplicationMessage.Topic}");
        c.PreviousDeviceDiscovered += (_, a) => events.Add($"PreviousDeviceDiscovered id={a.DiscoveryId} device={N(a.DeviceId)} auto={a.AutoDiscover != null}");
        c.MqttMessageMalformed += (_, _) => events.Add("Malformed");
        return events;

        Task Add(string s)
        {
            events.Add(s);
            return Task.CompletedTask;
        }
    }
}
