using ESPresense.Services;
using ESPresense.Utils;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using TextExtensions;

namespace ESPresense.Models
{
    public class AutoDiscovery
    {
        private readonly Device _dev;
        private readonly string _component;

        public AutoDiscovery(Device dev, string component)
        {
            _dev = dev;
            _component = component;
        }

        [JsonProperty("name")]
        string Name => _dev.Name ?? _dev.Id;

        [JsonProperty("unique_id")]
        string UniqueId => $"espresense-companion-{_dev.Id}";

        [JsonProperty("state_topic")]
        string StateTopic => $"espresense/companion/{_dev.Id}";

        [JsonProperty("json_attributes_topic")]
        string JsonAttributesTopic => $"espresense/companion/{_dev.Id}/attributes";

        [JsonProperty("status_topic")]
        string EntityStatusTopic => "espresense/companion/status";

        [JsonProperty("device")]
        private DeviceRecord Device => new DeviceRecord(_dev.Name, "ESPresense", "Companion", "0.0.0", new[] { $"espresense-{_dev.Id}" });

        bool _sent;

        public async Task Send(MqttCoordinator mc)
        {
            if (_sent) return;
            _sent = true;

            await mc.EnqueueAsync($"homeassistant/{_component}/{_dev.Id.ToSnakeCase()}/config", JsonConvert.SerializeObject(this, SerializerSettings.NullIgnore), retain: true);
        }
    }

    public record DeviceRecord(string? name, string manufacturer, string model, string sw_version, string[] identifiers);
}
