using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
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
            mqtt.DeviceConfigReceivedAsync += arg =>
            {
                _storeById.AddOrUpdate(arg.DeviceId, _ => arg.Payload, (_, _) => arg.Payload);
                if (arg.Payload.Id != null) _storeByAlias.AddOrUpdate(arg.Payload.Id, _ => arg.Payload, (_, _) => arg.Payload);
                return Task.CompletedTask;
            };

            await Task.Delay(-1, stoppingToken);
        }
    }
}
