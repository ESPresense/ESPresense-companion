using ESPresense.Services;

namespace ESPresense.Companion.Tests.Services;

/// <summary>Table test for the pure topic router: every topic family, plus what falls through (null).</summary>
public class MqttTopicRouterTests
{
    private const string T = "topic-placeholder";

    public static IEnumerable<TestCaseData> Routes()
    {
        TestCaseData Case(string topic, MqttRoute? expected) =>
            new TestCaseData(topic, expected).SetName($"{{m}}({topic})");

        yield return Case("espresense/rooms/node-1/telemetry", new NodeTelemetryRoute(T, "node-1"));
        yield return Case("espresense/rooms/node-1/status", new NodeStatusRoute(T, "node-1"));
        yield return Case("espresense/rooms/node-1/update_error", new NodeUpdateErrorRoute(T, "node-1"));
        yield return Case("espresense/rooms/node-1/absorption", new NodeSettingRoute(T, "node-1", "absorption"));
        yield return Case("espresense/rooms/*/absorption/set", new NodeSettingRoute(T, "*", "absorption"));
        yield return Case("espresense/rooms/*/telemetry", new NodeTelemetryRoute(T, "*"));
        yield return Case("espresense/rooms/*/absorption", new NodeSettingRoute(T, "*", "absorption"));
        yield return Case("espresense/devices/dev-1/node-1", new DeviceMessageRoute(T, "dev-1", "node-1"));
        yield return Case("espresense/settings/dev-1/config", new DeviceSettingsRoute(T, "dev-1"));
        yield return Case("espresense/companion/dev-1/attributes", new DeviceAttributesRoute(T, "dev-1"));
        yield return Case("homeassistant/device_tracker/espresense-dev-1/config", new DiscoveryRoute(T, "espresense-dev-1"));

        // Not ours: the coordinator raises MqttMessageReceivedAsync for these.
        yield return Case("espresense/rooms/node-1/absorption/set", null);
        yield return Case("espresense/rooms/node-1", null);
        yield return Case("espresense/devices/dev-1", null);
        yield return Case("espresense/devices/dev-1/node-1/extra", null);
        yield return Case("espresense/settings/dev-1/other", null);
        yield return Case("espresense/companion/status", null);
        yield return Case("espresense/companion/telemetry", null);
        yield return Case("espresense/companion/dev-1", null);
        yield return Case("espresense/companion/lease/some-lease", null);
        yield return Case("homeassistant/sensor/x/config", null);
        yield return Case("homeassistant/device_tracker/x/state", null);
        yield return Case("other/device_tracker/x/config", null);
        yield return Case("ESPRESENSE/rooms/node-1/telemetry", null);
        yield return Case("", null);
        yield return Case("some/other/topic", null);
    }

    [TestCaseSource(nameof(Routes))]
    public void Match_MapsTopicToRoute(string topic, object? expectedRoute)
    {
        var expected = (MqttRoute?)expectedRoute;
        var route = new MqttTopicRouter("homeassistant").Match(topic);

        // The route always carries the topic exactly as received.
        Assert.That(route, Is.EqualTo(expected == null ? null : expected with { Topic = topic }));
    }

    [Test]
    public void Match_UsesTheConfiguredDiscoveryPrefix()
    {
        var router = new MqttTopicRouter("custom-ha");

        Assert.That(router.Match("custom-ha/device_tracker/espresense-dev-1/config"),
            Is.EqualTo(new DiscoveryRoute("custom-ha/device_tracker/espresense-dev-1/config", "espresense-dev-1")));
        Assert.That(router.Match("homeassistant/device_tracker/espresense-dev-1/config"), Is.Null);
    }

    [Test]
    public void Match_DiscoveryWinsWhenThePrefixIsEspresense()
    {
        // Discovery is checked before the espresense/* families, as the original switch did.
        var router = new MqttTopicRouter("espresense");

        Assert.That(router.Match("espresense/device_tracker/x/config"), Is.EqualTo(new DiscoveryRoute("espresense/device_tracker/x/config", "x")));
    }
}
