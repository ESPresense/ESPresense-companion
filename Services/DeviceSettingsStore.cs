using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class DeviceSettingsStore(MqttCoordinator mqtt) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeById = new();
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeByAlias = new();

        public DeviceSettings? Get(string id)
        {
            _storeById.TryGetValue(id, out var dsId);
            _storeByAlias.TryGetValue(id, out var dsAlias);
            return dsId ?? dsAlias;
        }

        public async Task Set(string id, DeviceSettings ds)
        {
            ds.OriginalId = null;
            await mqtt.EnqueueAsync($"espresense/settings/{id}/config", JsonConvert.SerializeObject(ds, SerializerSettings.NullIgnore), true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await mqtt.SubscribeAsync("espresense/settings/#");

            mqtt.MqttMessageReceivedAsync += arg =>
            {
                var parts = arg.ApplicationMessage.Topic.Split('/');
                if (parts.Length >= 4 && parts[3] == "config")
                {
                    var ds = JsonConvert.DeserializeObject<DeviceSettings>(arg.ApplicationMessage.ConvertPayloadToString() ?? "");
                    if (ds == null) return Task.CompletedTask;
                    ds.OriginalId = parts[2];
                    _storeById.AddOrUpdate(parts[2], _ => ds, (_, _) => ds);
                    if (ds.Id != null) _storeByAlias.AddOrUpdate(ds.Id, _ => ds, (_, _) => ds);
                }
                return Task.CompletedTask;
            };

            await Task.Delay(-1, stoppingToken);
        }
    }
}
