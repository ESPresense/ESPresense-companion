using System.Text.Json;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// Differential check for #1854: inbound MQTT payloads parsed with <see cref="JsonDefaults.MqttRead"/> give the same
/// models Newtonsoft's <c>JsonConvert.DeserializeObject</c> gave, and fail where it failed.
/// </summary>
[TestFixture]
public class MqttReadCompatibilityTests
{
    // Shapes copied from current firmware (v5 reports and telemetry) with made-up ids, names and addresses.
    private static readonly string[] DeviceMessages =
    {
        """{"mac":"0123456789ab","id":"node:example","name":"Example","rssi@1m":-60,"rssi":-68.74,"rawRssi":-68.74,"rssiVar":41.81,"distance":2.26,"var":3.70,"int":262}""",
        """{"mac":"0123456789ac","id":"apple:1006:10-12","rssi@1m":-65,"rssi":-69.08,"rawRssi":-69.08,"rssiVar":36.46,"distance":1.46,"var":0.98,"int":450}""",
        """{"mac":"0123456789ad","id":"iBeacon:00000000-0000-0000-0000-000000000000-0-0","rssi@1m":-120,"rssi":-78.00,"rawRssi":-84.00,"distance":0.03,"var":0.00,"int":3809}""",
        """{"mac":"0123456789ae","id":"sensor","name":"Sensor","rssi@1m":-65,"rssi":-83.88,"rawRssi":-83.88,"rssiVar":20.67,"distance":5.00,"var":6.99,"int":2192,"mV":3026,"temp":23.00}""",
        """{"mac":"0123456789af","id":"node:abcdef","name":"abcdef","rssi@1m":-59,"rssi":-13.74,"rawRssi":-13.74,"rssiVar":1.73,"distance":0.02,"var":0.00,"close":true,"int":265}""",
        // Pre-v5 firmware: rxAdj instead of rawRssi
        """{"id":"phone:example","rssi@1m":-59,"rssi":-77,"raw":5.25,"distance":5.22,"rxAdj":5,"int":640}""",
        // Leniency the old parse had
        """{"RSSI":-70.5,"Distance":"1.5","rssi@1m":"-59","name":42}""",
        """{"rssi":-70, "distance":1, /* comment */ "name":true,}""",
        """{"rssi":"NaN","distance":"Infinity"}""",
        """{"name":false}""",
        """{"name":1.50}""",
        """{"name":1e3}""",
        """{"name":-0.0}""",
        """{"name":12345678901234567890}""",
        """{"rssi":-70,"distance":1,"name":null,"var":null,"rawRssi":null}""",
        """{}""",
        // Failures either way
        """{"rssi":null}""",
        """{"rssi":"loud"}""",
        """{"rssi":true}""",
        """{"rssi":[1]}""",
        """{"name":{"a":1}}""",
        """{bad""",
        """[1]""",
        "null",
        "",
    };

    private static readonly string[] Telemetry =
    {
        """{"ip":"192.0.2.10","uptime":125964,"reset":"sw","firm":"esp32c3","board":"Example Plug","rssi":-60,"app0":1966080,"ver":"v5.0.0b6","adverts":16268929,"seen":6271870,"reported":1669537,"failed":50,"freeHeap":72460,"maxHeap":36864,"fingerprints":62,"scanStack":2144,"loopStack":2720,"bleStack":2416}""",
        """{"ip":"192.0.2.11","uptime":3600,"firm":"esp32","rssi":-71,"ver":"v4.2.1","count":5,"adverts":1000,"seen":900,"reported":800,"freeHeap":100000,"maxAllocHeap":60000,"memFrag":12.5,"scanHighWater":1500,"csi_fps":9.8,"charging":"no"}""",
        """{"ip":"192.0.2.12","uptime":"60","rssi":"-50","ver":5,"memFrag":"3.5"}""",
        """{"uptime":3000000000}""",
        """{"uptime":1.0}""",
        """{"uptime":1.5}""",
        """{"rssi":-60.4}""",
        """{"freeHeap":null}""",
        """{"ip":null,"ver":null}""",
        "{bad",
    };

    private static readonly string[] Settings =
    {
        """{"id":"phone:example","name":"Example Phone","rssi@1m":-75}""",
        """{"id":"node:example","name":"Example"}""",
        """{"name":"Anchor","x":1.5,"y":2,"z":"0.5"}""",
        """{"id":"phone:example","rssi@1m":-59.0}""",
        """{"id":"phone:example","rssi@1m":-59.5}""",
        """{"id":"phone:example","rssi@1m":null}""",
        """{"ID":"phone:example","Name":"Upper"}""",
        """{"id":123}""",
        """{"hasAnchor":true,"originalId":"from-payload"}""",
        "{}",
        "{bad",
        "",
    };

    private static readonly string[] Attributes =
    {
        """{"source_type":"espresense","x":8.66,"y":10.28,"z":2.21,"confidence":61,"fixes":14,"best_scenario":"MultiFloor","last_seen":"2026-11-05T14:02:40.2543849Z"}""",
        """{"last_seen":"2026-11-05T14:02:40+02:00"}""",
        """{"last_seen":"2026-11-05T14:02:40"}""",
        """{"last_seen":"2026-11-05"}""",
        """{"last_seen":"5 Nov 2026 14:02"}""",
        """{"last_seen":"not a date"}""",
        """{"last_seen":null}""",
        """{"last_seen":1730815360}""",
        """{"x":1}""",
        "[1]",
        "{bad",
    };

    private static IEnumerable<TestCaseData> Cases() =>
        DeviceMessages.Select(p => new TestCaseData(typeof(DeviceMessage), p))
            .Concat(Telemetry.Select(p => new TestCaseData(typeof(NodeTelemetry), p)))
            .Concat(Settings.Select(p => new TestCaseData(typeof(DeviceSettings), p)))
            .Concat(Attributes.Select(p => new TestCaseData(typeof(Dictionary<string, object>), p)));

    [TestCaseSource(nameof(Cases))]
    public void Payload_ParsesLikeNewtonsoft(Type type, string payload)
    {
        Assert.That(MqttRead(type, payload), Is.EqualTo(Newtonsoft(type, payload)));
    }

    [Test]
    public void Telemetry_ReadsTheNodesNames_AndTheApiKeepsItsOwn()
    {
        var telemetry = System.Text.Json.JsonSerializer.Deserialize<NodeTelemetry>("""{"ver":"v5.0.0","firm":"esp32c3","memFrag":12.5}""", JsonDefaults.MqttRead)!;
        Assert.That((telemetry.Version, telemetry.Firmware, telemetry.MemoryFragmentation), Is.EqualTo(("v5.0.0", "esp32c3", 12.5)));

        var api = System.Text.Json.JsonSerializer.Serialize(telemetry, JsonDefaults.Web);
        Assert.That(api, Does.Contain("\"version\":\"v5.0.0\"").And.Contain("\"firmware\":\"esp32c3\"").And.Contain("\"memoryFragmentation\":12.5"));
        Assert.That(api, Does.Not.Contain("\"ver\"").And.Not.Contain("\"firm\"").And.Not.Contain("memFrag"));
    }

    /// <summary>
    /// The same check over a capture of real traffic: <c>mosquitto_sub -v -t 'espresense/devices/#'
    /// -t 'espresense/rooms/+/telemetry' -t 'espresense/settings/+/config' -t 'espresense/companion/+/attributes' &gt; capture.txt</c>,
    /// then run with <c>MQTT_CAPTURE=capture.txt</c>. Explicit because the capture is someone's house.
    /// </summary>
    [Test, Explicit("Needs MQTT_CAPTURE, a mosquitto_sub -v capture")]
    public void Capture_ParsesLikeNewtonsoft()
    {
        var path = Environment.GetEnvironmentVariable("MQTT_CAPTURE");
        if (string.IsNullOrEmpty(path)) Assert.Ignore("MQTT_CAPTURE not set");

        var checkedCount = 0;
        var mismatches = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var space = line.IndexOf(' ');
            var (topic, payload) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..]);
            Type? type = topic switch
            {
                _ when topic.EndsWith("/telemetry") => typeof(NodeTelemetry),
                _ when topic.EndsWith("/attributes") => typeof(Dictionary<string, object>),
                _ when topic.StartsWith("espresense/settings/") => typeof(DeviceSettings),
                _ when topic.StartsWith("espresense/devices/") => typeof(DeviceMessage),
                _ => null
            };
            if (type == null) continue;
            checkedCount++;
            var (expected, actual) = (Newtonsoft(type, payload), MqttRead(type, payload));
            if (expected != actual) mismatches.Add($"{topic}: {payload}\n  newtonsoft {expected}\n  mqttRead   {actual}");
        }

        TestContext.Out.WriteLine($"{checkedCount} payloads compared");
        Assert.That(checkedCount, Is.GreaterThan(0));
        Assert.That(mismatches.Take(20), Is.Empty, $"{mismatches.Count} of {checkedCount} differ");
    }

    // Both sides become one comparable string: the parsed model as JSON, or "throws".
    private static readonly JsonSerializerOptions Canonical = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static string Newtonsoft(Type type, string payload)
    {
        object? parsed;
        try { parsed = JsonConvert.DeserializeObject(payload, type, NewtonsoftReference.Parse); }
        catch (Newtonsoft.Json.JsonException) { return "throws"; }
        if (parsed == null) return "throws"; // MqttPayload turns a null result into an MqttMessageProcessingException

        // The old parse handed DeviceTracker a DateTime when it recognised one; that is the value it restored.
        if (parsed is Dictionary<string, object> attributes)
            return LastSeen(attributes.GetValueOrDefault("last_seen") is var v && v is DateTime dt ? dt : v as string is { } s && DateTime.TryParse(s, out var p) ? p : null);
        return System.Text.Json.JsonSerializer.Serialize(parsed, type, Canonical);
    }

    private static string MqttRead(Type type, string payload)
    {
        object parsed;
        try
        {
            var route = new DeviceMessageRoute("espresense/devices/d/n", "d", "n");
            parsed = typeof(MqttPayload).GetMethod(nameof(MqttPayload.Deserialize))!.MakeGenericMethod(type)
                .Invoke(null, new object?[] { route, payload, "Test", "null", "failed" })!;
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is MqttMessageProcessingException) { return "throws"; }

        if (parsed is Dictionary<string, object> attributes)
            return LastSeen(DeviceTracker.ReadLastSeen(attributes.GetValueOrDefault("last_seen")));
        return System.Text.Json.JsonSerializer.Serialize(parsed, type, Canonical);
    }

    private static string LastSeen(DateTime? value) => value is { } v ? $"last_seen {v:O} {v.Kind}" : "last_seen none";
}
