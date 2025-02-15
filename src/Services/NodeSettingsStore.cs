﻿using System.Collections.Concurrent;
using ESPresense.Models;
using Serilog;

namespace ESPresense.Services
{
    public class NodeSettingsStore(MqttCoordinator mqtt, ILogger<NodeSettingsStore> logger) : BackgroundService
    {
        private static bool ParseBool(string value)
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

        public Models.NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new Models.NodeSettings(id);
        }

        public async Task Set(string id, Models.NodeSettings ns)
        {
            var retain = id == "*";
            var old = Get(id);

            // Updating settings
            if (ds.Updating.AutoUpdate == null || ds.Updating.AutoUpdate != old.Updating.AutoUpdate)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/auto_update/set", ds.Updating.AutoUpdate == true ? "ON" : "OFF", retain);
            if (ds.Updating.Prerelease == null || ds.Updating.Prerelease != old.Updating.Prerelease)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/prerelease/set", ds.Updating.Prerelease == true ? "ON" : "OFF", retain);

            // Scanning settings
            if (ds.Scanning.ForgetAfterMs == null || ds.Scanning.ForgetAfterMs != old.Scanning.ForgetAfterMs)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/forget_after_ms/set", $"{ds.Scanning.ForgetAfterMs}", retain);

            // Counting settings
            if (ds.Counting.IdPrefixes == null || ds.Counting.IdPrefixes != old.Counting.IdPrefixes)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/count_ids/set", $"{ds.Counting.IdPrefixes}", retain);
            if (ds.Counting.MinDistance == null || ds.Counting.MinDistance != old.Counting.MinDistance)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/count_min_dist/set", $"{ds.Counting.MinDistance:0.00}", retain);
            if (ds.Counting.MaxDistance == null || ds.Counting.MaxDistance != old.Counting.MaxDistance)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/count_max_dist/set", $"{ds.Counting.MaxDistance:0.00}", retain);
            if (ds.Counting.MinMs == null || ds.Counting.MinMs != old.Counting.MinMs)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/count_ms/set", $"{ds.Counting.MinMs}", retain);

            // Filtering settings
            if (ds.Filtering.IncludeIds == null || ds.Filtering.IncludeIds != old.Filtering.IncludeIds)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/include/set", $"{ds.Filtering.IncludeIds}", retain);
            if (ds.Filtering.ExcludeIds == null || ds.Filtering.ExcludeIds != old.Filtering.ExcludeIds)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/exclude/set", $"{ds.Filtering.ExcludeIds}", retain);
            if (ds.Filtering.MaxDistance == null || ds.Filtering.MaxDistance != old.Filtering.MaxDistance)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/max_distance/set", $"{ds.Filtering.MaxDistance:0.00}", retain);
            if (ds.Filtering.SkipDistance == null || ds.Filtering.SkipDistance != old.Filtering.SkipDistance)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/skip_distance/set", $"{ds.Filtering.SkipDistance:0.00}", retain);
            if (ds.Filtering.SkipMs == null || ds.Filtering.SkipMs != old.Filtering.SkipMs)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/skip_ms/set", $"{ds.Filtering.SkipMs}", retain);

            // Calibration settings
            if (ds.Calibration.Absorption == null || ds.Calibration.Absorption != old.Calibration.Absorption)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/absorption/set", $"{ds.Calibration.Absorption:0.00}", retain);
            if (ds.Calibration.RxAdjRssi == null || ds.Calibration.RxAdjRssi != old.Calibration.RxAdjRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/rx_adj_rssi/set", $"{ds.Calibration.RxAdjRssi}", retain);
            if (ds.Calibration.TxRefRssi == null || ds.Calibration.TxRefRssi != old.Calibration.TxRefRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/tx_ref_rssi/set", $"{ds.Calibration.TxRefRssi}", retain);
            if (ds.Calibration.RxRefRssi == null || ds.Calibration.RxRefRssi != old.Calibration.RxRefRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/ref_rssi/set", $"{ds.Calibration.RxRefRssi}", retain);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            mqtt.NodeSettingReceivedAsync += arg =>
            {
                try
                {
                    var ns = Get(arg.NodeId);
                    switch (arg.Setting)
                    {
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

        public async Task Update(string id)
        {
            await mqtt.EnqueueAsync($"espresense/rooms/{id}/update/set", "PRESS");
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
