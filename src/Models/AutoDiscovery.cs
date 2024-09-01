using ESPresense.Services;
using ESPresense.Utils;
using Newtonsoft.Json;
using TextExtensions;

namespace ESPresense.Models;

public class AutoDiscovery(Device dev, string name, string component, string sourceType = null)
{
    private bool _sent;

    [JsonProperty("name")]
    private string Name => name;

    [JsonProperty("unique_id")]
    private string UniqueId => $"espresense-companion-{dev.Id}";

    [JsonProperty("state_topic")]
    private string StateTopic => $"espresense/companion/{dev.Id}";

    [JsonProperty("json_attributes_topic")]
    private string JsonAttributesTopic => $"espresense/companion/{dev.Id}/attributes";

    [JsonProperty("status_topic")]
    private string EntityStatusTopic => "espresense/companion/status";

    [JsonProperty("device")]
    private DeviceRecord Device => new(dev.Name ?? dev.Id, "ESPresense", "Companion", "0.0.0", new[] { $"espresense-{dev.Id}" });

    [JsonProperty("origin")]
    private OriginRecord Origin => new("ESPresense Companion");

    [JsonProperty("source_type")]
    private string SourceType => sourceType;

    public async Task Send(MqttCoordinator mc)
    {
        if (_sent) return;
        _sent = true;

        await mc.EnqueueAsync($"homeassistant/{component}/{dev.Id.ToSnakeCase()}/config", JsonConvert.SerializeObject(this, SerializerSettings.NullIgnore), true);
    }
}

public record DeviceRecord(string? name, string manufacturer, string model, string sw_version, string[] identifiers);

public record OriginRecord(string? name);