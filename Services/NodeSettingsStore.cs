using System.Collections.Concurrent;
using System.Reflection.Metadata;
using ESPresense.Models;
using ESPresense.Utils;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace ESPresense.Services
{
    public class NodeSettingsStore : BackgroundService
    {
        private readonly MqttConnectionFactory _mqttConnectionFactory;

        private readonly ConcurrentDictionary<string, NodeSettings> _storeById = new();

        private IManagedMqttClient? _mc;

        public NodeSettingsStore(MqttConnectionFactory mqttConnectionFactory)
        {
            _mqttConnectionFactory = mqttConnectionFactory;
        }

        public NodeSettings Get(string id)
        {
            return _storeById.TryGetValue(id, out var ns) ? ns.Clone() : new NodeSettings();
        }

        public async Task Set(string id, NodeSettings ds)
        {
            var old = Get(id);
            if (ds.Absorption != null && ds.Absorption != old.Absorption)
                await _mc.EnqueueAsync("espresense/rooms/" + id + "/absorption/set", $"{ds.Absorption:0.00}");
            if (ds.RxAdjRssi != null && ds.RxAdjRssi != old.RxAdjRssi)
                await _mc.EnqueueAsync("espresense/rooms/" + id + "/rx_adj_rssi/set", $"{ds.RxAdjRssi}");
            if (ds.TxRefRssi != null && ds.TxRefRssi != old.TxRefRssi)
                await _mc.EnqueueAsync("espresense/rooms/" + id + "/tx_ref_rssi/set", $"{ds.TxRefRssi}");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var mc = _mc = await _mqttConnectionFactory.GetClient(false);
            await mc.SubscribeAsync("espresense/rooms/+/+");

            mc.ApplicationMessageReceivedAsync += async arg =>
            {
                var parts = arg.ApplicationMessage.Topic.Split('/');
                if (parts.Length != 4 || parts[3] == "telemetry") return;

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
                        return;
                }

                _storeById.AddOrUpdate(parts[2], _ => ns, (_, _) => ns);
            };

            await Task.Delay(-1, stoppingToken);
        }
    }
}
