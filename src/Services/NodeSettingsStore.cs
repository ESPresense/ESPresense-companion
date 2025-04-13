using System.Collections.Concurrent;
using ESPresense.Models;
using Serilog;

namespace ESPresense.Services
{
    public class NodeSettingsStore(MqttCoordinator mqtt, ILogger<NodeSettingsStore> logger) : BackgroundService
    {
        private static bool ParseBool(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            value = value.Trim().ToUpperInvariant();
            return value switch
            {
                "TRUE" or "1" or "ON" => true,
                "FALSE" or "0" or "OFF" => false,
                _ => bool.Parse(value)
            };
        }
        private readonly ConcurrentDictionary<string, NodeSettings> _storeById = new();

        public NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new NodeSettings(id);
        }

        /// <summary>
        /// Asynchronously updates a node's settings by sending updates only for properties that have changed.
        /// </summary>
        /// <param name="id">The identifier of the node. If set to "*", the updates are marked to be retained.</param>
        /// <param name="ds">A NodeSettings object containing the new configuration values; only properties that differ from the current settings are updated.</param>
        /// <remarks>
        /// Compares the new settings with the current stored settings and, for each changed property,
        /// sends an update via the MQTT coordinator along with the previous value.
        /// </remarks>
        public async Task Set(string id, NodeSettings ds)
        {
            var retain = id == "*";
            var old = Get(id);

            if (ds.Name != null && ds.Name != old.Name)
                await mqtt.UpdateSetting(id, "name", ds.Name, retain, old.Name);

            // Updating settings
            if (ds.Updating.AutoUpdate != null && ds.Updating.AutoUpdate != old.Updating.AutoUpdate)
                await mqtt.UpdateSetting(id, "auto_update", ds.Updating.AutoUpdate, retain, old.Updating.AutoUpdate);

            if (ds.Updating.Prerelease != null && ds.Updating.Prerelease != old.Updating.Prerelease)
                await mqtt.UpdateSetting(id, "prerelease", ds.Updating.Prerelease, retain, old.Updating.Prerelease);

            // Scanning settings
            if (ds.Scanning.ForgetAfterMs != null && ds.Scanning.ForgetAfterMs != old.Scanning.ForgetAfterMs)
                await mqtt.UpdateSetting(id, "forget_after_ms", ds.Scanning.ForgetAfterMs, retain, old.Scanning.ForgetAfterMs);

            // Counting settings
            if (ds.Counting.IdPrefixes != null && ds.Counting.IdPrefixes != old.Counting.IdPrefixes)
                await mqtt.UpdateSetting(id, "count_ids", ds.Counting.IdPrefixes, retain, old.Counting.IdPrefixes);

            if (ds.Counting.MinDistance != null && ds.Counting.MinDistance != old.Counting.MinDistance)
                await mqtt.UpdateSetting(id, "count_min_dist", ds.Counting.MinDistance, retain, old.Counting.MinDistance);

            if (ds.Counting.MaxDistance != null && ds.Counting.MaxDistance != old.Counting.MaxDistance)
                await mqtt.UpdateSetting(id, "count_max_dist", ds.Counting.MaxDistance, retain, old.Counting.MaxDistance);

            if (ds.Counting.MinMs != null && ds.Counting.MinMs != old.Counting.MinMs)
                await mqtt.UpdateSetting(id, "count_ms", ds.Counting.MinMs, retain, old.Counting.MinMs);

            // Filtering settings
            if (ds.Filtering.IncludeIds != null && ds.Filtering.IncludeIds != old.Filtering.IncludeIds)
                await mqtt.UpdateSetting(id, "include", ds.Filtering.IncludeIds, retain, old.Filtering.IncludeIds);

            if (ds.Filtering.ExcludeIds != null && ds.Filtering.ExcludeIds != old.Filtering.ExcludeIds)
                await mqtt.UpdateSetting(id, "exclude", ds.Filtering.ExcludeIds, retain, old.Filtering.ExcludeIds);

            if (ds.Filtering.MaxDistance != null && ds.Filtering.MaxDistance != old.Filtering.MaxDistance)
                await mqtt.UpdateSetting(id, "max_distance", ds.Filtering.MaxDistance, retain, old.Filtering.MaxDistance);

            if (ds.Filtering.SkipDistance != null && ds.Filtering.SkipDistance != old.Filtering.SkipDistance)
                await mqtt.UpdateSetting(id, "skip_distance", ds.Filtering.SkipDistance, retain, old.Filtering.SkipDistance);

            if (ds.Filtering.SkipMs != null && ds.Filtering.SkipMs != old.Filtering.SkipMs)
                await mqtt.UpdateSetting(id, "skip_ms", ds.Filtering.SkipMs, retain, old.Filtering.SkipMs);

            // Calibration settings
            if (ds.Calibration.Absorption != null && ds.Calibration.Absorption != old.Calibration.Absorption)
                await mqtt.UpdateSetting(id, "absorption", ds.Calibration.Absorption, retain, old.Calibration.Absorption);

            if (ds.Calibration.RxAdjRssi != null && ds.Calibration.RxAdjRssi != old.Calibration.RxAdjRssi)
                await mqtt.UpdateSetting(id, "rx_adj_rssi", ds.Calibration.RxAdjRssi, retain, old.Calibration.RxAdjRssi);

            if (ds.Calibration.TxRefRssi != null && ds.Calibration.TxRefRssi != old.Calibration.TxRefRssi)
                await mqtt.UpdateSetting(id, "tx_ref_rssi", ds.Calibration.TxRefRssi, retain, old.Calibration.TxRefRssi);

            if (ds.Calibration.RxRefRssi != null && ds.Calibration.RxRefRssi != old.Calibration.RxRefRssi)
                await mqtt.UpdateSetting(id, "ref_rssi", ds.Calibration.RxRefRssi, retain, old.Calibration.RxRefRssi);
        }

        /// <summary>
        /// Asynchronously listens for and processes incoming node setting updates via MQTT.
        /// </summary>
        /// <remarks>
        /// Registers an event handler on the MQTT coordinator's NodeSettingReceivedAsync event to parse and update node settings
        /// based on incoming messages. Depending on the setting type, the handler updates various properties of a node's settings in an in-memory store.
        /// The method runs indefinitely until cancellation is requested via the provided token.
        /// </remarks>
        /// <param name="stoppingToken">A token that signals the request to stop the service gracefully.</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            mqtt.NodeSettingReceivedAsync += arg =>
            {
                Log.Debug("Received {0} for {1}: {2}", arg.Setting, arg.NodeId, arg.Payload);
                try
                {
                    var ns = Get(arg.NodeId);
                    switch (arg.Setting)
                    {
                        case "name":
                            ns.Name = arg.Payload;
                            break;

                        // Updating settings
                        case "auto_update":
                            ns.Updating.AutoUpdate = ParseBool(arg.Payload);
                            break;
                        case "prerelease":
                            ns.Updating.Prerelease = ParseBool(arg.Payload);
                            break;

                        // Counting settings
                        case "count_ids":
                            ns.Counting.IdPrefixes = arg.Payload;
                            break;
                        case "count_min_dist":
                            ns.Counting.MinDistance = double.Parse(arg.Payload);
                            break;
                        case "count_max_dist":
                            ns.Counting.MaxDistance = double.Parse(arg.Payload);
                            break;

                        // Filtering settings
                        case "include":
                            ns.Filtering.IncludeIds = arg.Payload;
                            break;
                        case "exclude":
                            ns.Filtering.ExcludeIds = arg.Payload;
                            break;
                        case "max_distance":
                            ns.Filtering.MaxDistance = double.Parse(arg.Payload);
                            break;
                        case "skip_distance":
                            ns.Filtering.SkipDistance = double.Parse(arg.Payload);
                            break;
                        case "skip_ms":
                            ns.Filtering.SkipMs = int.Parse(arg.Payload);
                            break;

                        // Calibration settings
                        case "absorption":
                            ns.Calibration.Absorption = double.Parse(arg.Payload);
                            break;
                        case "rx_adj_rssi":
                            ns.Calibration.RxAdjRssi = int.Parse(arg.Payload);
                            break;
                        case "tx_ref_rssi":
                            ns.Calibration.TxRefRssi = int.Parse(arg.Payload);
                            break;
                        case "ref_rssi":
                            ns.Calibration.RxRefRssi = int.Parse(arg.Payload);
                            break;

                        default:
                            return Task.CompletedTask;
                    }

                    _storeById.AddOrUpdate(arg.NodeId, _ => ns, (_, _) => ns);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error parsing {0} for {1}", arg.Setting, arg.NodeId);
                }
                return Task.CompletedTask;
            };

            await Task.Delay(-1, stoppingToken);
        }

        public async Task Update(string id, string? url)
        {
            await mqtt.EnqueueAsync($"espresense/rooms/{id}/update/set", url ?? "PRESS");
        }

        public async Task Arduino(string id, bool on)
        {
            await mqtt.EnqueueAsync($"espresense/rooms/{id}/arduino_ota/set", on ? "ON" : "OFF");
        }

        public async Task Restart(string id)
        {
            await mqtt.EnqueueAsync($"espresense/rooms/{id}/restart/set", "PRESS");
        }
    }
}
