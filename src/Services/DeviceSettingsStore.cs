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
                ApplyToDevice(arg.DeviceId, arg.Payload);
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
                device.Track = true;
            }
        }

        private DeviceAnchor? BuildAnchor(DeviceSettings settings)
        {
            if (!settings.HasAnchor)
                return null;

            if (!settings.X.HasValue || !settings.Y.HasValue || !settings.Z.HasValue)
                return null;

            var location = new MathNet.Spatial.Euclidean.Point3D(settings.X.Value, settings.Y.Value, settings.Z.Value);
            var floor = FindFloor(location);
            var room = FindRoom(floor, location);
            return new DeviceAnchor(location, floor, room);
        }

        private Floor? FindFloor(MathNet.Spatial.Euclidean.Point3D location)
        {
            foreach (var floor in state.Floors.Values)
            {
                if (floor.Bounds is not { Length: >= 2 })
                    continue;

                var min = floor.Bounds[0];
                var max = floor.Bounds[1];

                if (location.X >= min.X && location.X <= max.X &&
                    location.Y >= min.Y && location.Y <= max.Y &&
                    location.Z >= min.Z && location.Z <= max.Z)
                {
                    return floor;
                }
            }

            return null;
        }

        private Room? FindRoom(Floor? floor, MathNet.Spatial.Euclidean.Point3D location)
        {
            if (floor == null)
                return null;

            foreach (var room in floor.Rooms.Values)
            {
                if (room.Polygon?.EnclosesPoint(location.ToPoint2D()) ?? false)
                {
                    return room;
                }
            }

            return null;
        }
    }
}
