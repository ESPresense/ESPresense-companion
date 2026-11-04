using System.Text.Json;
using ESPresense.Models;

namespace ESPresense.Utils;

/// <summary>
/// The JSON the Companion itself publishes to MQTT, built in one place so the wire format can be pinned by tests.
/// </summary>
internal static class MqttPayloads
{
    /// <summary><c>{discovery_topic}/{component}/{id}/config</c></summary>
    public static string Discovery(AutoDiscovery.DiscoveryRecord record) => JsonSerializer.Serialize(record, JsonDefaults.Mqtt);

    /// <summary><c>espresense/companion/telemetry</c></summary>
    public static string Telemetry(Telemetry telemetry) => JsonSerializer.Serialize(telemetry, JsonDefaults.Mqtt);

    /// <summary><c>espresense/settings/{id}/config</c></summary>
    public static string DeviceSettings(DeviceSettings settings) => JsonSerializer.Serialize(settings, JsonDefaults.Mqtt);

    /// <summary><c>espresense/companion/{id}/attributes</c> when only the provisional last-seen stamp is known</summary>
    public static string LastSeen(DateTime lastSeen) => JsonSerializer.Serialize(new { last_seen = lastSeen }, JsonDefaults.Verbatim);
}
