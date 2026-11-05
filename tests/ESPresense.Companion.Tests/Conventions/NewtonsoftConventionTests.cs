using System.Text.RegularExpressions;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// One JSON rule per boundary (#1846): System.Text.Json for everything the Companion owns. Newtonsoft stays only
/// where BUILDING.md says why; this list and that section must agree.
/// </summary>
[TestFixture]
public class NewtonsoftConventionTests
{
    private static readonly Regex Newtonsoft = new(@"\bNewtonsoft\b|\bJsonConvert\b|\[JsonProperty\(|\bSerializerSettings\.NullIgnore\b", RegexOptions.Compiled);

    private static readonly string[] Allowed =
    {
        // Third-party contract: Flurl's Newtonsoft serializer reads the HA Supervisor API, whose numeric
        // "port" lands in a string member (Newtonsoft coerces it, System.Text.Json would throw).
        "Program.cs",

        // TODO(#1854): MqttMessageHandlers parses device messages, node telemetry, device settings and device
        // attributes with Newtonsoft; the models carry Newtonsoft attributes for that parse. The attributes
        // arrive as Dictionary<string, object>, and DeviceTracker restores last_seen only when Newtonsoft has
        // already made it a DateTime, so the parse moves to JsonDefaults.MqttRead with #1854.
        "Services/Mqtt/MqttMessageHandlers.cs",
        "Services/MqttCoordinator.cs",            // catches the JsonSerializationException that parse throws
        "Models/DeviceMessage.cs",
        "Models/NodeTelemetry.cs",
        "Models/DeviceSettings.cs",
    };

    [Test]
    public void Newtonsoft_IsOnlyUsedWhereDocumented()
    {
        Assert.That(SourceScan.FilesMatching(Newtonsoft), Is.EquivalentTo(Allowed),
            "Use System.Text.Json with a JsonDefaults options instance. If Newtonsoft really is needed, add the file here and justify it in BUILDING.md.");
    }
}
