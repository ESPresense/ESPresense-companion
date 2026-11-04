using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// Characterization tests for #1846: the exact bytes each boundary emits, pinned before the JSON
/// serializers were consolidated. A change here is a wire-format change and needs its own issue.
/// All fixtures are synthetic.
/// </summary>
[TestFixture]
public class WireFormatTests
{
    // Characters where Newtonsoft and System.Text.Json's default encoder disagree: HTML-sensitive ASCII,
    // an apostrophe, non-ASCII letters, an emoji (surrogate pair), a quote, a backslash and control characters.
    private const string AwkwardName = "Kid's <Tag> & \"Ünïcode\" \\ 🙂\n\t\u0001";

    private static JsonSerializerOptions Rest => JsonDefaults.Web;
    private static JsonSerializerOptions WebSocket => JsonDefaults.WebSocket;

    private static Device AnchoredDevice()
    {
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30)) { Name = "Test Tag" };
        device.ConfiguredRefRssi = -59;
        device.SetAnchor(new DeviceAnchor(new Point3D(1, 2.5, 0.75), null, null));
        return device;
    }

    private static DeviceSettings SampleDeviceSettings() => new()
    {
        Id = "test-device-1",
        Name = AwkwardName,
        RefRssi = -59,
        X = 2.0,
        Y = 0.1,
        Z = 0
    };

    private static NodeSettings SampleNodeSettings() => new("test-node-1", "Test Node")
    {
        Updating = { AutoUpdate = true, Prerelease = false },
        Scanning = { ForgetAfterMs = 150000 },
        Counting = { IdPrefixes = "phone:", MinDistance = 0, MaxDistance = 2.0 },
        Filtering = { IncludeIds = "a b", SkipDistance = 0.5, SkipMs = 5000 },
        Calibration = { RxAdjRssi = -3, Absorption = 2.7, TxRefRssi = -59 }
    };

    private static ConfigMqtt SampleMqtt() => new()
    {
        Host = "broker.test",
        Port = 1883,
        Ssl = false,
        Username = "test-user",
        Password = "not-on-the-wire",
        CaCertPath = null,
        ClientId = "test-client",
        ReadOnly = true
    };

    // ---- REST (ASP.NET MVC options from Program.cs) ----

    [Test]
    public void Rest_Device()
    {
        Assert.That(JsonSerializer.Serialize(AnchoredDevice(), Rest), Is.EqualTo(
            """{"id":"test-device-1","name":"Test Tag","isAnchored":true,"nodes":{},"room":null,"floor":null,"confidence":100,"scale":null,"fixes":0,"lastSeen":null,"location":{"x":1,"y":2.5,"z":0.75},"timeout":30000,"rssi@1m":-59,"measuredRssi@1m":null}"""));
    }

    [Test]
    public void Rest_DeviceSettings()
    {
        Assert.That(JsonSerializer.Serialize(SampleDeviceSettings(), Rest), Is.EqualTo(
            """{"id":"test-device-1","originalId":null,"name":"Kid\u0027s \u003CTag\u003E \u0026 \u0022\u00DCn\u00EFcode\u0022 \\ \uD83D\uDE42\n\t\u0001","rssi@1m":-59,"x":2,"y":0.1,"z":0}"""));
    }

    [Test]
    public void Rest_NodeSettings()
    {
        Assert.That(JsonSerializer.Serialize(SampleNodeSettings(), Rest), Is.EqualTo(
            """{"id":"test-node-1","name":"Test Node","updating":{"autoUpdate":true,"prerelease":false},"scanning":{"forgetAfterMs":150000},"counting":{"idPrefixes":"phone:","minDistance":0,"maxDistance":2,"minMs":null},"filtering":{"includeIds":"a b","excludeIds":null,"maxDistance":null,"skipDistance":0.5,"skipMs":5000},"calibration":{"rxRefRssi":null,"rxAdjRssi":-3,"usesRxOffset":false,"absorption":2.7,"txRefRssi":-59}}"""));
    }

    [Test]
    public void Rest_ConfigMqtt()
    {
        Assert.That(JsonSerializer.Serialize(SampleMqtt(), Rest), Is.EqualTo(
            """{"host":"broker.test","port":1883,"ssl":false,"insecure":false,"caCertPath":null,"username":"test-user","clientId":"test-client","discoveryTopic":"homeassistant","readOnly":true}"""));
    }

    [Test]
    public void Rest_EnumsAreStringsAndNaNIsNamed()
    {
        Assert.That(JsonSerializer.Serialize(new { outcome = LocatorOutcome.Solved, value = double.NaN }, Rest),
            Is.EqualTo("""{"outcome":"Solved","value":"NaN"}"""));
    }

    // ---- WebSocket (/ws events, StateController) ----

    [Test]
    public void WebSocket_DeviceChanged()
    {
        Assert.That(JsonSerializer.Serialize(new { type = "deviceChanged", data = AnchoredDevice() }, WebSocket), Is.EqualTo(
            """{"type":"deviceChanged","data":{"id":"test-device-1","name":"Test Tag","isAnchored":true,"nodes":{},"room":null,"floor":null,"confidence":100,"scale":null,"fixes":0,"lastSeen":null,"location":{"x":1,"y":2.5,"z":0.75},"timeout":30000,"rssi@1m":-59,"measuredRssi@1m":null}}"""));
    }

    [Test]
    public void WebSocket_EnumsAreNumbersAndNaNIsNamed()
    {
        // Unlike REST, the WebSocket options never had a string-enum converter.
        Assert.That(JsonSerializer.Serialize(new { outcome = LocatorOutcome.Solved, value = double.NaN }, WebSocket),
            Is.EqualTo("""{"outcome":0,"value":"NaN"}"""));
    }

    [Test]
    public void WebSocket_CommandIsReadCaseInsensitively()
    {
        var command = JsonSerializer.Deserialize<WebSocketCommand>("""{"COMMAND":"changeFilter","type":"showAll","Value":"true"}""", WebSocket);
        Assert.That(command!.Command, Is.EqualTo("changeFilter"));
        Assert.That(command.Type, Is.EqualTo("showAll"));
        Assert.That(command.Value, Is.EqualTo("true"));
    }

    [Test]
    public void WebSocket_FirmwareProgress()
    {
        Assert.That(FirmwareController.ProgressMessage(0, AwkwardName), Is.EqualTo(
            "{\"percentComplete\":0,\"message\":\"Kid's <Tag> & \\\"Ünïcode\\\" \\\\ 🙂\\n\\t\\u0001\"}"));
    }

    // ---- MCP (McpResources) ----

    [Test]
    public async Task Mcp_ReturnsTheRestJson()
    {
        // Since #1849 every MCP tool returns exactly what the matching /api endpoint does (McpRestParityTests
        // covers devices, config and calibration); this pins the three remaining tools to the REST options.
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "wire-mcp", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.yaml"),
            "mqtt:\n  host: broker.test\n  port: 1883\n  username: test-user\n  password: not-on-the-wire\n  client_id: test-client\n");
        try
        {
            var configLoader = new ConfigLoader(dir);
            await configLoader.LoadAsync();
            var mqtt = new Mock<IMqttCoordinator>().Object;
            var nts = new NodeTelemetryStore(mqtt);
            var state = new State(configLoader, nts);
            var nss = new NodeSettingsStore(mqtt, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
            var telemetry = new TelemetryService(null!);
            telemetry.Telemetry.Ip = "192.0.2.10";
            telemetry.Telemetry.UnknownNodes.Add("unknown-node");
            telemetry.IncrementMessages();
            var sut = new McpResources(state, configLoader, nss, nts, new DeviceEnricher(new DeviceSettingsStore(mqtt, state)), telemetry,
                new FirmwareUpdateJobService(nss, nts, new HttpClient(), Mock.Of<Microsoft.Extensions.Logging.ILogger<FirmwareUpdateJobService>>()),
                new FirmwareCatalogService(new HttpClient(), Mock.Of<IFirmwareTypeStore>(), nts),
                new NodeStateMapper(nts, Mock.Of<IFirmwareTypeStore>()),
                new CalibrationService(state, nss));

            Assert.That(await sut.GetConfigTool(), Is.EqualTo(JsonSerializer.Serialize(configLoader.Config, Rest)));
            Assert.That(await sut.GetConfigTool(), Does.Contain("""{"host":"broker.test","port":1883,"""));
            Assert.That(await sut.GetTelemetryTool(), Is.EqualTo(
                """{"ip":"192.0.2.10","messages":1,"devices":0,"tracked":0,"skipped":0,"malformed":0,"moved":0}"""));
            Assert.That(await sut.GetNodeSettingsTool("test-node-1"), Is.EqualTo(
                """{"id":"test-node-1","name":null,"updating":{"autoUpdate":null,"prerelease":null},"scanning":{"forgetAfterMs":null},"counting":{"idPrefixes":null,"minDistance":null,"maxDistance":null,"minMs":null},"filtering":{"includeIds":null,"excludeIds":null,"maxDistance":null,"skipDistance":null,"skipMs":null},"calibration":{"rxRefRssi":null,"rxAdjRssi":null,"usesRxOffset":false,"absorption":null,"txRefRssi":null}}"""));

            configLoader.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // ---- MQTT (what the Companion publishes) ----

    [Test]
    public void Mqtt_Discovery()
    {
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30)) { Name = AwkwardName };
        var discovery = new AutoDiscovery("device_tracker", device, null, "bluetooth");
        Assert.That(MqttPayloads.Discovery(discovery.Message), Is.EqualTo(
            "{\"name\":\"Kid's <Tag> & \\\"Ünïcode\\\" \\\\ 🙂\\n\\t\\u0001\",\"unique_id\":\"espresense-companion-test-device-1\",\"state_topic\":\"espresense/companion/test-device-1\",\"json_attributes_topic\":\"espresense/companion/test-device-1/attributes\",\"availability_topic\":\"espresense/companion/status\",\"device\":{\"name\":\"Kid's <Tag> & \\\"Ünïcode\\\" \\\\ 🙂\\n\\t\\u0001\",\"manufacturer\":\"ESPresense\",\"model\":\"Companion\",\"sw_version\":\"1.0.0\",\"identifiers\":[\"espresense-test-device-1\"]},\"origin\":{\"name\":\"ESPresense Companion\"},\"source_type\":\"bluetooth\"}"));
    }

    [Test]
    public void Mqtt_Discovery_NameUnset()
    {
        // Null members are omitted; the device record falls back to the id.
        var device = new Device("test-device-2", null, TimeSpan.FromSeconds(30));
        var discovery = new AutoDiscovery("device_tracker", device);
        Assert.That(MqttPayloads.Discovery(discovery.Message), Is.EqualTo(
            """{"unique_id":"espresense-companion-test-device-2","state_topic":"espresense/companion/test-device-2","json_attributes_topic":"espresense/companion/test-device-2/attributes","availability_topic":"espresense/companion/status","device":{"name":"test-device-2","manufacturer":"ESPresense","model":"Companion","sw_version":"1.0.0","identifiers":["espresense-test-device-2"]},"origin":{"name":"ESPresense Companion"}}"""));
    }

    [Test]
    public async Task Mqtt_Discovery_SentToConfigTopic()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        mqtt.SetupGet(m => m.DiscoveryTopic).Returns("homeassistant");
        var device = new Device("test-device-2", null, TimeSpan.FromSeconds(30));
        var discovery = new AutoDiscovery("device_tracker", device);

        await discovery.Send(mqtt.Object);

        mqtt.Verify(m => m.EnqueueAsync("homeassistant/device_tracker/espresense_test_device_2/config",
            MqttPayloads.Discovery(discovery.Message), true), Times.Once);
    }

    [Test]
    public void Mqtt_Telemetry()
    {
        var telemetry = new Telemetry { Ip = "192.0.2.10" };
        telemetry.Messages = 12;
        telemetry.Malformed = 1;
        telemetry.UnknownNodes.Add("unknown-node");
        Assert.That(MqttPayloads.Telemetry(telemetry), Is.EqualTo(
            """{"unknownNodes":["unknown-node"],"ip":"192.0.2.10","messages":12,"malformed":1}"""));
    }

    [Test]
    public void Mqtt_Telemetry_Empty()
    {
        Assert.That(MqttPayloads.Telemetry(new Telemetry()), Is.EqualTo("""{"unknownNodes":[]}"""));
    }

    [Test]
    public void Mqtt_DeviceSettings()
    {
        // Whole doubles keep a ".0"; zero and nulls are omitted (DefaultValueHandling.Ignore).
        Assert.That(MqttPayloads.DeviceSettings(SampleDeviceSettings()), Is.EqualTo(
            "{\"id\":\"test-device-1\",\"name\":\"Kid's <Tag> & \\\"Ünïcode\\\" \\\\ 🙂\\n\\t\\u0001\",\"rssi@1m\":-59,\"x\":2.0,\"y\":0.1,\"z\":0.0}"));
    }

    [Test]
    public void Mqtt_DeviceSettings_Doubles()
    {
        var ds = new DeviceSettings { Id = "d", X = 1e21, Y = -0.000001, Z = 12.345678901234567 };
        Assert.That(MqttPayloads.DeviceSettings(ds), Is.EqualTo("""{"id":"d","x":1E+21,"y":-1E-06,"z":12.345678901234567}"""));
    }

    [Test]
    public async Task Mqtt_DeviceSettings_PublishedBySet()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var store = new DeviceSettingsStore(mqtt.Object, null!);
        var ds = new DeviceSettings { Id = "test-device-1", OriginalId = "ignored", RefRssi = -61 };

        await store.Set("test-device-1", ds);

        mqtt.Verify(m => m.EnqueueAsync("espresense/settings/test-device-1/config", """{"id":"test-device-1","rssi@1m":-61}""", true), Times.Once);
    }

    [Test]
    public void Mqtt_LastSeen()
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
        Assert.That(MqttPayloads.LastSeen(now), Is.EqualTo("""{"last_seen":"2026-01-02T03:04:05.123456Z"}"""));
        Assert.That(MqttPayloads.LastSeen(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), Is.EqualTo("""{"last_seen":"2026-01-02T03:04:05Z"}"""));
    }

    // ---- MQTT discovery messages read back from the broker ----

    [Test]
    public void Mqtt_DiscoveryRoundTrips()
    {
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30)) { Name = AwkwardName };
        var sent = new AutoDiscovery("device_tracker", device, null, "bluetooth");

        Assert.That(AutoDiscovery.TryDeserialize("homeassistant/device_tracker/espresense_test_device_1/config", MqttPayloads.Discovery(sent.Message), out var read), Is.True);
        Assert.That(read!.Component, Is.EqualTo("device_tracker"));
        Assert.That(read.DiscoveryId, Is.EqualTo("espresense_test_device_1"));
        Assert.That(MqttPayloads.Discovery(read.Message), Is.EqualTo(MqttPayloads.Discovery(sent.Message)));
    }

    [TestCase("""{"origin":{"name":"Some Other Bridge"},"name":"x"}""", TestName = "Discovery from another origin is ignored")]
    [TestCase("""{"name":"x"}""", TestName = "Discovery without an origin is ignored")]
    [TestCase("null", TestName = "Discovery payload null")]
    [TestCase("not json", TestName = "Discovery payload not JSON")]
    [TestCase("""{"origin":{"name":"ESPresense Companion"},"device":{"identifiers":"not-an-array"}}""", TestName = "Discovery with wrongly typed identifiers")]
    public void Mqtt_DiscoveryIgnored(string payload)
    {
        Assert.That(AutoDiscovery.TryDeserialize("homeassistant/device_tracker/some_id/config", payload, out var read), Is.False);
        Assert.That(read, Is.Null);
    }

    [Test]
    public void Mqtt_DiscoveryReadLeniently()
    {
        // Other bridges publish under the same prefix; Newtonsoft matched keys case-insensitively,
        // turned numbers into strings, and tolerated comments and trailing commas.
        const string payload = """
            {
              // a comment
              "Origin": {"Name": "ESPresense Companion", "sw": 5},
              "name": 12,
              "device": {"sw_version": 1.5, "identifiers": ["a", "b",],},
              "unknown": {"nested": [1, 2, 3]},
            }
            """;
        Assert.That(AutoDiscovery.TryDeserialize("homeassistant/sensor/some_id/config", payload, out var read), Is.True);
        Assert.That(read!.Message.Name, Is.EqualTo("12"));
        Assert.That(read.Message.Device!.SwVersion, Is.EqualTo("1.5"));
        Assert.That(read.Message.Device.Identifiers, Is.EqualTo(new[] { "a", "b" }));
    }
}
