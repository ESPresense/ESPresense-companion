using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense.Services;

public class NodeTelemetryStore : BackgroundService
{
    private readonly MqttCoordinator _mqttCoordinator;

    private readonly ConcurrentDictionary<string, NodeTelemetry> _storeById = new();
    private readonly ConcurrentDictionary<string, bool> _onlineById = new();

    public NodeTelemetryStore(MqttCoordinator mqttCoordinator)
    {
        _mqttCoordinator = mqttCoordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqttCoordinator.NodeTelemetryReceivedAsync += arg =>
        {
            _storeById.AddOrUpdate(arg.NodeId, _ => arg.Payload, (_, _) => arg.Payload);
            return Task.CompletedTask;
        };

        _mqttCoordinator.NodeStatusReceivedAsync += arg =>
        {
            _onlineById.AddOrUpdate(arg.NodeId, _ => arg.Online, (_, _) => arg.Online);
            return Task.CompletedTask;
        };

        await Task.Delay(-1, stoppingToken);
    }

    public NodeTelemetry? Get(string id)
    {
        _storeById.TryGetValue(id, out var ds);
        return ds;
    }

    public bool Online(string id)
    {
        return _onlineById.TryGetValue(id, out var online) && online;
    }

    public async Task Delete(string id)
    {
        _storeById.TryRemove(id, out _);
        _onlineById.TryRemove(id, out _);

        await _mqttCoordinator.EnqueueAsync($"espresense/rooms/{id}/telemetry", null, true);
        await _mqttCoordinator.EnqueueAsync($"espresense/rooms/{id}/status", null, true);
    }
}