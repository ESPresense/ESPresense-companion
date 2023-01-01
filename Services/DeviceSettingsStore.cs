using System.Collections.Concurrent;
using ESPresense.Models;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class DeviceSettingsStore : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        private readonly ConcurrentDictionary<string, DeviceSettings> _storeById = new();
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeByAlias = new();

        private IManagedMqttClient? _mc;

        public DeviceSettingsStore(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }


        public Task<DeviceSettings?> Get(string id)
        {
            _storeById.TryGetValue(id, out var dsId);
            _storeByAlias.TryGetValue(id, out var dsAlias);
            return Task.FromResult(dsId ?? dsAlias);
        }

        public async Task Set(string id, DeviceSettings ds)
        {
            JsonSerializerSettings jss = new()
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            };
            ds.OriginalId = null;
            await _mc.EnqueueAsync("espresense/settings/" + id + "/config", JsonConvert.SerializeObject(ds, jss), MqttQualityOfServiceLevel.AtMostOnce, true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            using var mc = _mc = await scope.ServiceProvider.GetRequiredService<Task<IManagedMqttClient>>();

            await mc.SubscribeAsync("espresense/settings/#");

            mc.ApplicationMessageReceivedAsync += arg =>
            {
                var parts = arg.ApplicationMessage.Topic.Split('/');
                if (parts.Length >= 4 && parts[3] == "config")
                {
      
                    var ds = JsonConvert.DeserializeObject<DeviceSettings>(arg.ApplicationMessage.ConvertPayloadToString());
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
