using System.Collections.Concurrent;
using ESPresense.Models;
using ESPresense.Utils;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class NodeTelemetryStore : BackgroundService
{
    private readonly MqttConnectionFactory _mqttConnectionFactory;

    private readonly ConcurrentDictionary<string, NodeTelemetry> _storeById = new();

    private IManagedMqttClient? _mc;

    public NodeTelemetryStore(MqttConnectionFactory mqttConnectionFactory)
    {
        _mqttConnectionFactory = mqttConnectionFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var mc = _mc = await _mqttConnectionFactory.GetClient(false);
        await mc.SubscribeAsync("espresense/rooms/+/telemetry");

        mc.ApplicationMessageReceivedAsync += arg =>
        {
            var parts = arg.ApplicationMessage.Topic.Split('/');
            if (parts is not [_, _, _, "telemetry"]) return Task.CompletedTask;
            var ds = JsonConvert.DeserializeObject<NodeTelemetry>(arg.ApplicationMessage.ConvertPayloadToString() ?? "");
            if (ds == null) return Task.CompletedTask;
            _storeById.AddOrUpdate(parts[2], _ => ds, (_, _) => ds);
            return Task.CompletedTask;
        };

        await Task.Delay(-1, stoppingToken);
    }

    public NodeTelemetry? Get(string? id)
    {
        _storeById.TryGetValue(id ?? "", out var ds);
        return ds;
    }
}