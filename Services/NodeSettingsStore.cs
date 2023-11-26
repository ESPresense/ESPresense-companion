using System.Collections.Concurrent;
using ESPresense.Models;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;

namespace ESPresense.Services
{
    public class NodeSettingsStore(MqttCoordinator mqtt, ILogger<NodeSettingsStore> logger) : BackgroundService
    {
        private readonly ConcurrentDictionary<string, NodeSettings> _storeById = new();

        public NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new NodeSettings();
        }

        public async Task Set(string id, NodeSettings ds)
        {
            var old = Get(id);
            if (ds.Absorption != null && ds.Absorption != old.Absorption)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/absorption/set", $"{ds.Absorption:0.00}");
            if (ds.RxAdjRssi != null && ds.RxAdjRssi != old.RxAdjRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/rx_adj_rssi/set", $"{ds.RxAdjRssi}");
            if (ds.TxRefRssi != null && ds.TxRefRssi != old.TxRefRssi)
                await mqtt.EnqueueAsync($"espresense/rooms/{id}/tx_ref_rssi/set", $"{ds.TxRefRssi}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await mqtt.SubscribeAsync("espresense/rooms/+/+");

            mqtt.MqttMessageReceivedAsync += arg =>
            {
                try
                {
                    var parts = arg.ApplicationMessage.Topic.Split('/');
                    if (parts.Length != 4 || parts[3] == "telemetry") return Task.CompletedTask;

                    var pay = arg.ApplicationMessage.ConvertPayloadToString() ?? "";
                    var ns = Get(parts[2]);
                    switch (parts[3])
                    {
                        case "absorption":
                            ns.Absorption = double.Parse(pay);
                            break;
                        case "rx_adj_rssi":
                            ns.RxAdjRssi = int.Parse(pay);
                            break;
                        case "tx_ref_rssi":
                            ns.TxRefRssi = int.Parse(pay);
                            break;
                        case "max_distance":
                            ns.MaxDistance = double.Parse(pay);
                            break;
                        default:
                            return Task.CompletedTask;
                    }

                    _storeById.AddOrUpdate(parts[2], _ => ns, (_, _) => ns);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error parsing {0}", arg.ApplicationMessage.Topic);
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
