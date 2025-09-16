using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class DeviceSettingsStore(IMqttCoordinator mqtt) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeById = new();
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeByAlias = new();

        public virtual DeviceSettings? Get(string id)
        {
            _storeById.TryGetValue(id, out var dsId);
            _storeByAlias.TryGetValue(id, out var dsAlias);
            return dsId ?? dsAlias;
        }

        public virtual async Task Set(string id, DeviceSettings ds)
        {
            var existing = Get(id);

            // If we found an existing entry and the id being written to is different from the OriginalId,
            // then 'id' is an alias and we should reject writes to aliases to prevent duplicate entries
            if (existing?.OriginalId != null && existing.OriginalId != id)
            {
                throw new InvalidOperationException($"Cannot write to alias '{id}'. Please write to the original ID '{existing.OriginalId}' instead.");
            }

            ds.OriginalId = null; // Clear OriginalId before storing/publishing
            await mqtt.EnqueueAsync($"espresense/settings/{id}/config", JsonConvert.SerializeObject(ds, SerializerSettings.NullIgnore), true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            mqtt.DeviceConfigReceivedAsync += async arg =>
            {
                _storeById.AddOrUpdate(arg.DeviceId, _ => arg.Payload, (_, _) => arg.Payload);
                if (arg.Payload.Id != null) _storeByAlias.AddOrUpdate(arg.Payload.Id, _ => arg.Payload, (_, _) => arg.Payload);
            };

            await Task.Delay(-1, stoppingToken);
        }
    }
}
