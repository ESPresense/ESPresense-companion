using System.Collections.Concurrent;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class DeviceSettingsStore(IMqttCoordinator mqtt, State state) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeById = new();
        private readonly ConcurrentDictionary<string, DeviceSettings> _storeByAlias = new();

        public virtual DeviceSettings? Get(string id)
        {
            _storeById.TryGetValue(id, out var dsId);
            _storeByAlias.TryGetValue(id, out var dsAlias);
            return dsId ?? dsAlias;
        }

        /// <summary>
        /// Raised with the device ids a settings change made dead: the alias a device used to have, and the raw
        /// settings key once it has an alias (nodes report the alias from then on).
        /// </summary>
        public event Func<IReadOnlyList<string>, Task>? DeviceIdsRetiredAsync;

        /// <summary>
        /// True when <paramref name="id"/> is a settings key that now has a different alias, and no other settings
        /// entry uses it as its alias. Nodes no longer report a device under such an id.
        /// </summary>
        public virtual bool IsRetired(string id)
        {
            return _storeById.TryGetValue(id, out var ds) && ds.Id != null && ds.Id != id && !_storeByAlias.ContainsKey(id);
        }

        private bool IsClaimed(string id)
        {
            if (_storeByAlias.ContainsKey(id)) return true;
            return _storeById.TryGetValue(id, out var ds) && (ds.Id == null || ds.Id == id);
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
                // If this device previously advertised a different alias, drop that alias entry (only while it
                // still points at the previous settings object, so an alias taken over by another device is
                // left alone). Otherwise Get(oldAlias) keeps resolving here and Set(oldAlias) keeps throwing
                // "Cannot write to alias".
                _storeById.TryGetValue(arg.DeviceId, out var previous);
                var droppedAlias = previous?.Id != null && previous.Id != arg.Payload.Id
                    && _storeByAlias.TryRemove(new KeyValuePair<string, DeviceSettings>(previous.Id, previous));

                _storeById.AddOrUpdate(arg.DeviceId, _ => arg.Payload, (_, _) => arg.Payload);
                if (arg.Payload.Id != null) _storeByAlias.AddOrUpdate(arg.Payload.Id, _ => arg.Payload, (_, _) => arg.Payload);
                ApplyToDevice(arg.DeviceId, arg.Payload);

                // Nodes now report this device under its new id, so the old alias and the raw key are dead and
                // their Home Assistant entities would linger forever.
                if (previous?.Id == arg.Payload.Id) return;
                var retired = new List<string>();
                if (droppedAlias && !IsClaimed(previous!.Id!)) retired.Add(previous.Id!);
                if (IsRetired(arg.DeviceId)) retired.Add(arg.DeviceId);
                if (retired.Count > 0 && DeviceIdsRetiredAsync != null) await DeviceIdsRetiredAsync(retired);
            };

            await Task.Delay(-1, stoppingToken);
        }

        public void ApplyToDevice(string deviceId, DeviceSettings settings)
        {
            if (!state.Devices.TryGetValue(deviceId, out var device))
            {
                if (settings.Id != null && state.Devices.TryGetValue(settings.Id, out var aliasDevice))
                {
                    device = aliasDevice;
                }
                else
                {
                    return;
                }
            }

            device.ConfiguredRefRssi = settings.RefRssi;
            if (!string.IsNullOrWhiteSpace(settings.Name))
            {
                device.Name = settings.Name;
            }

            var anchor = BuildAnchor(settings);
            device.SetAnchor(anchor);
            if (anchor != null)
            {
                device.Check = true;
            }
        }

        private DeviceAnchor? BuildAnchor(DeviceSettings settings)
        {
            if (!settings.HasAnchor)
                return null;

            if (!settings.X.HasValue || !settings.Y.HasValue || !settings.Z.HasValue)
                return null;

            // Validate coordinates are not NaN or Infinity
            if (double.IsNaN(settings.X.Value) || double.IsInfinity(settings.X.Value) ||
                double.IsNaN(settings.Y.Value) || double.IsInfinity(settings.Y.Value) ||
                double.IsNaN(settings.Z.Value) || double.IsInfinity(settings.Z.Value))
                return null;

            var location = new MathNet.Spatial.Euclidean.Point3D(settings.X.Value, settings.Y.Value, settings.Z.Value);
            var (floor, room) = SpatialUtils.FindFloorAndRoom(location, state.Floors.Values);
            return new DeviceAnchor(location, floor, room);
        }
    }
}
