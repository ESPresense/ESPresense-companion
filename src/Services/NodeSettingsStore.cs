using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense.Services
{
    public class NodeSettingsStore(MqttCoordinator mqtt, ILogger<NodeSettingsStore> logger) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, Models.NodeSettings> _storeById = new();

        public Models.NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new Models.NodeSettings(id);
        }

        public async Task Set(string id, Models.NodeSettings ns)
        {
            var oCs = Get(id).Calibration;
            var nCs = ns.Calibration;
            if (nCs.Absorption != null && nCs.Absorption != oCs.Absorption)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/absorption/set", $"{nCs.Absorption:0.00}");
            if (nCs.RxAdjRssi != null && nCs.RxAdjRssi != oCs.RxAdjRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/rx_adj_rssi/set", $"{nCs.RxAdjRssi}");
            if (nCs.TxRefRssi != null && nCs.TxRefRssi != oCs.TxRefRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/tx_ref_rssi/set", $"{nCs.TxRefRssi}");
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
                            ns.Calibration.Absorption = double.Parse(arg.Payload);
                            break;
                        case "rx_adj_rssi":
                            ns.Calibration.RxAdjRssi = int.Parse(arg.Payload);
                            break;
                        case "tx_ref_rssi":
                            ns.Calibration.TxRefRssi = int.Parse(arg.Payload);
                            break;
                        case "max_distance":
                            ns.Filtering.MaxDistance = double.Parse(arg.Payload);
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
