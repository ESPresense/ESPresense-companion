using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class DeviceSettingsStore : BackgroundService
    {
        private readonly MqttConnectionFactory _mqttConnectionFactory;

        private readonly ConcurrentDictionary<string, DeviceSettings> _storeById = new();
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeByAlias = new();

        private IManagedMqttClient? _mc;

        public DeviceSettingsStore(MqttConnectionFactory mqttConnectionFactory)
        {
            _mqttConnectionFactory = mqttConnectionFactory;
        }

        public Task<DeviceSettings?> Get(string id)
        {
            _storeById.TryGetValue(id, out var dsId);
            _storeByAlias.TryGetValue(id, out var dsAlias);
            return Task.FromResult(dsId ?? dsAlias);
        }

        public async Task Set(string id, DeviceSettings ds)
        {
            ds.OriginalId = null;
            await _mc.EnqueueAsync("espresense/settings/" + id + "/config", JsonConvert.SerializeObject(ds, SerializerSettings.NullIgnore), MqttQualityOfServiceLevel.AtMostOnce, true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var mc = _mc = await _mqttConnectionFactory.GetClient(false);
            await mc.SubscribeAsync("espresense/settings/#");

            mc.ApplicationMessageReceivedAsync += arg =>
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
