using ESPresense.Services;
using ESPresense.Utils;
using Newtonsoft.Json;
using Serilog;
using TextExtensions;

namespace ESPresense.Models;

public class AutoDiscovery
{
    private bool _sent;

    public DiscoveryRecord Message { get; set; }
    public string DiscoveryId { get; internal set; }
    public string Component { get; internal set; }

    public AutoDiscovery(string component, Device dev, string? discoveryId = null, string? sourceType = null)
    {
        DiscoveryId = discoveryId ?? $"espresense-{dev.Id}".ToSnakeCase();
        Component = component;

        Message = new DiscoveryRecord()
        {
            Name = dev.Name,
            UniqueId = $"espresense-companion-{dev.Id}",
            StateTopic = $"espresense/companion/{dev.Id}",
            JsonAttributesTopic = $"espresense/companion/{dev.Id}/attributes",
            EntityStatusTopic = "espresense/companion/status",
            Device = new DeviceRecord()
            {
                Name = dev.Name ?? dev.Id,
                Manufacturer = "ESPresense",
                Model = "Companion",
                SwVersion = "1.0.0",
                Identifiers = new[] { $"espresense-{dev.Id}" }
            },
            Origin = new OriginRecord { Name = "ESPresense Companion" },
            SourceType = sourceType
        };
    }

    public AutoDiscovery(string component, string discoveryId, DiscoveryRecord message)
    {
        DiscoveryId = discoveryId;
        Component = component;
        Message = message;
    }

    public async Task Send(IMqttCoordinator mqtt)
    {
        if (_sent) return;
        _sent = true;

        Log.Debug($"[+] Discovery {Component} {DiscoveryId}");
        await mqtt.EnqueueAsync($"{mqtt.DiscoveryTopic}/{Component}/{DiscoveryId}/config",
            JsonConvert.SerializeObject(Message, SerializerSettings.NullIgnore), true);
    }

    public async Task Delete(IMqttCoordinator mqtt)
    {
        Log.Debug($"[-] Discovery {Component} {DiscoveryId}");
        await mqtt.EnqueueAsync($"{mqtt.DiscoveryTopic}/{Component}/{DiscoveryId}/config", null, true);
    }

    internal static bool TryDeserialize(string topic, string payload, out AutoDiscovery? msg, string discoveryTopic = "homeassistant")
    {
        var parts = topic.Split("/");
        if (parts.Length != 4 || parts[0] != discoveryTopic || parts[3] != "config")
        {
            Log.Debug("Invalid topic structure");
            msg = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            msg = null;
            return false;
        }

        try
        {
            var discovery = JsonConvert.DeserializeObject<DiscoveryRecord>(payload);
            if (discovery == null)
            {
                Log.Warning("Failed to deserialize discovery message");
                msg = null;
                return false;
            }

            if (discovery?.Origin?.Name != "ESPresense Companion")
            {
                var origin = discovery?.Origin?.Name;
                if (origin != null) Log.Debug($"Ignoring discovery message from origin: {origin}");
                msg = null;
                return false;
            }

            msg = new AutoDiscovery(parts[1], parts[2], discovery);
            return true;
        }
        catch (JsonException ex)
        {
            Log.Warning("Failed to deserialize discovery message: {0}", ex.Message);
            msg = null;
            return false;
        }
    }

    public class DiscoveryRecord
    {
        [JsonProperty("name")] public string? Name { get; set; }

        [JsonProperty("unique_id")] public string? UniqueId { get; set; }

        [JsonProperty("state_topic")] public string? StateTopic { get; set; }

        [JsonProperty("json_attributes_topic")] public string? JsonAttributesTopic { get; set; }

        [JsonProperty("status_topic")] public string? EntityStatusTopic { get; set; }

        [JsonProperty("device")] public DeviceRecord? Device { get; set; }

        [JsonProperty("origin")] public OriginRecord? Origin { get; set; }

        [JsonProperty("source_type")] public string? SourceType { get; set; }
    }

    public class DeviceRecord
    {
        [JsonProperty("name")] public string? Name { get; set; }

        [JsonProperty("manufacturer")] public string? Manufacturer { get; set; }

        [JsonProperty("model")] public string? Model { get; set; }

        [JsonProperty("sw_version")] public string? SwVersion { get; set; }

        [JsonProperty("identifiers")] public string[]? Identifiers { get; set; }
    }

    public class OriginRecord
    {
        [JsonProperty("name")] public string? Name { get; set; }
    }
}