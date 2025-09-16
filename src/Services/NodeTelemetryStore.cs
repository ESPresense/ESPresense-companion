using System.Collections.Concurrent;
using ESPresense.Models;

namespace ESPresense.Services;

public class NodeTelemetryStore : BackgroundService
{
    private readonly IMqttCoordinator _mqttCoordinator;

    private readonly ConcurrentDictionary<string, NodeTelemetry> _teleById = new();
    private readonly ConcurrentDictionary<string, bool> _onlineById = new();

    /// <summary>
    /// Initializes a new instance of <see cref="NodeTelemetryStore"/> and stores the provided MQTT coordinator reference.
    /// </summary>
    /// <remarks>
    /// The coordinator is retained for subscribing to node telemetry and status events and for publishing deletion messages.
    /// </remarks>
    public NodeTelemetryStore(IMqttCoordinator mqttCoordinator)
    {
        _mqttCoordinator = mqttCoordinator;
    }

    /// <summary>
    /// Background loop that registers MQTT event handlers to maintain in-memory node telemetry and online-status caches.
    /// </summary>
    /// <remarks>
    /// Subscribes to the coordinator's telemetry and status events to add/update or remove entries in the internal
    /// thread-safe caches (_teleById and _onlineById). The method then waits indefinitely to keep the background
    /// service alive until cancellation is requested.
    /// </remarks>
    /// <param name="stoppingToken">Cancellation token used to stop the background service and exit the indefinite wait.</param>
    /// <returns>A task that completes when <paramref name="stoppingToken"/> is cancelled.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqttCoordinator.NodeTelemetryReceivedAsync += arg =>
        {
            _teleById.AddOrUpdate(arg.NodeId, _ => arg.Payload, (_, _) => arg.Payload);
            return Task.CompletedTask;
        };

        _mqttCoordinator.NodeTelemetryRemovedAsync += arg =>
        {
            _teleById.TryRemove(arg.NodeId, out _);
            return Task.CompletedTask;
        };

        _mqttCoordinator.NodeStatusReceivedAsync += arg =>
        {
            _onlineById.AddOrUpdate(arg.NodeId, _ => arg.Online, (_, _) => arg.Online);
            return Task.CompletedTask;
        };

        _mqttCoordinator.NodeStatusRemovedAsync += arg =>
        {
            _onlineById.TryRemove(arg.NodeId, out _);
            return Task.CompletedTask;
        };

        await Task.Delay(-1, stoppingToken);
    }

    /// <summary>
    /// Retrieves the cached telemetry for a node by its identifier.
    /// </summary>
    /// <param name="id">The node identifier.</param>
    /// <returns>The cached <see cref="NodeTelemetry"/> for the node, or <c>null</c> if none is available.</returns>
    public virtual NodeTelemetry? Get(string id)
    {
        _teleById.TryGetValue(id, out var ds);
        return ds;
    }

    /// <summary>
    /// Returns whether the node with the given id is currently marked online in the in-memory cache.
    /// </summary>
    /// <param name="id">The node identifier.</param>
    /// <returns>True if the node is known and marked online; otherwise false.</returns>
    public virtual bool Online(string id)
    {
        return _onlineById.TryGetValue(id, out var online) && online;
    }

    /// <summary>
    /// Publishes retained null messages to the node's telemetry and status MQTT topics to purge their retained values.
    /// </summary>
    /// <param name="id">The node/room identifier used to construct the MQTT topics.</param>
    /// <returns>A task that completes when both purge messages have been enqueued.</returns>
    public virtual async Task Delete(string id)
    {
        await _mqttCoordinator.EnqueueAsync($"espresense/rooms/{id}/telemetry", null, true);
        await _mqttCoordinator.EnqueueAsync($"espresense/rooms/{id}/status", null, true);
    }
}