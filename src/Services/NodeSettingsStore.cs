using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense.Services
{
    public class NodeSettingsStore(MqttCoordinator mqtt, ILogger<NodeSettingsStore> logger) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, NodeSettings> _storeById = new();

        public NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new NodeSettings(id);
        }

        public async Task Set(string id, NodeSettings ds)
        {
            var old = Get(id);
            if (ds.Absorption == null || ds.Calibration.Absorption != old.Calibration.Absorption)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/absorption/set", $"{ds.Calibration.Absorption:0.00}");
            if (ds.RxAdjRssi == null || ds.Calibration.RxAdjRssi != old.Calibration.RxAdjRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/rx_adj_rssi/set", $"{ds.Calibration.RxAdjRssi}");
            if (ds.TxRefRssi == null || ds.Calibration.TxRefRssi != old.Calibration.TxRefRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/tx_ref_rssi/set", $"{ds.Calibration.TxRefRssi}");
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
                        case "absorption":
                            ns.Absorption = double.Parse(arg.Payload);
                            break;
                        case "rx_adj_rssi":
                            ns.RxAdjRssi = int.Parse(arg.Payload);
                            break;
                        case "tx_ref_rssi":
                            ns.TxRefRssi = int.Parse(arg.Payload);
                            break;
                        case "max_distance":
                            ns.MaxDistance = double.Parse(arg.Payload);
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
