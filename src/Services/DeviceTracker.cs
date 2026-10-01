using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using Serilog;

namespace ESPresense.Services;

public class DeviceTracker(State state, IMqttCoordinator mqtt, TelemetryService tele, GlobalEventDispatcher globalEventDispatcher, DeviceSettingsStore deviceSettingsStore) : BackgroundService
{
    // Coalescing work sets: a device heard by N nodes is marked N times but processed/located once per pass.
    private readonly DirtyDeviceSet _toProcess = new();
    private readonly DirtyDeviceSet _toLocate = new();

    // Discovery ids whose retained config we cleared ourselves (untrack). The clear echoes back through our own
    // subscription and must not be mistaken for an external delete. Entries expire after SelfClearTtl.
    private readonly ConcurrentDictionary<string, DateTime> _selfClearedDiscoveryIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SelfClearTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Attaches MQTT event handlers to manage device discovery, messages and attributes, then runs background processing loops.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token that stops the background processing tasks.</param>
    /// <remarks>
    /// - Subscribes handlers to MQTT events to:
    ///   - Forward raw device messages to the global event dispatcher.
    ///   - Count malformed messages in telemetry.
    ///   - Handle discovery and deletion of device_tracker autodiscovery entries (creates Device entries for discovered trackers).
    ///   - Process incoming device messages: update node and device state, telemetry counters, and enqueue devices for processing or locating.
    ///   - Restore a device's LastSeen from attributes when available.
    /// - Starts and awaits two long-running background tasks: ProcessDevicesAsync and CheckIdleDevicesAsync, which consume internal dirty sets to evaluate and locate devices.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.DeviceMessageReceivedAsync += (e) =>
        {
            globalEventDispatcher.OnDeviceMessageReceived(e);
            return Task.CompletedTask;
        };

        mqtt.MqttMessageMalformed += (s, e) => { tele.IncrementMalformedMessages(); };

        mqtt.PreviousDeviceDiscovered += (s, arg) => OnPreviousDeviceDiscovered(arg);
        mqtt.DeviceMessageReceivedAsync += OnDeviceMessageReceivedAsync;

        // Handle device attributes to restore LastSeen values
        mqtt.DeviceAttributesReceivedAsync += async arg =>
        {
            if (state.Devices.TryGetValue(arg.DeviceId, out var device))
            {
                // Check if the attributes contain last_seen
                if (arg.Attributes.TryGetValue("last_seen", out var lastSeenObj) && lastSeenObj != null)
                {
                    try
                    {
                        DateTime? lastSeen = null;

                        // Handle different formats of last_seen
                        if (lastSeenObj is DateTime dateTime)
                        {
                            lastSeen = dateTime;
                        }
                        else if (lastSeenObj is string lastSeenStr && !string.IsNullOrEmpty(lastSeenStr))
                        {
                            if (DateTime.TryParse(lastSeenStr, out var parsedDate))
                            {
                                lastSeen = parsedDate;
                            }
                        }

                        if (device.LastSeen == null)
                        {
                            device.LastSeen = lastSeen;
                            Log.Information("Restored Last Seen for device {DeviceId} to {LastSeen}", arg.DeviceId, lastSeen);
                        }
                    }
                    catch (Exception ex) { Log.Warning(ex, "Failed to parse last_seen for device {DeviceId}", arg.DeviceId); }
                }
            }
        };

        // Start background tasks to process devices asynchronously
        var processTask = ProcessDevicesAsync(stoppingToken);
        var idleCheckTask = CheckIdleDevicesAsync(stoppingToken);

        await Task.WhenAll(processTask, idleCheckTask);
    }

    /// <summary>
    /// Handles a Home Assistant discovery message previously published by the companion: creates a tracked
    /// device for a device_tracker entry, or removes the device when the retained message was cleared.
    /// </summary>
    internal void OnPreviousDeviceDiscovered(PreviousDeviceDiscoveredEventArgs arg)
    {
        if (arg.AutoDiscover == null)
        {
            if (_selfClearedDiscoveryIds.TryRemove(arg.DiscoveryId, out _))
            {
                // Echo of the clear we published when the device stopped being tracked; the device stays.
                Log.Debug("Ignoring our own discovery clear for {DiscoveryId}", arg.DiscoveryId);
                return;
            }

            // The retained discovery message was cleared externally: remove the device it advertised. The topic
            // only carries the discovery id, so resolve the device through the discovery entries it published.
            var deviceId = arg.DeviceId ?? FindDeviceIdByDiscoveryId(arg.DiscoveryId);
            if (deviceId != null && state.Devices.TryRemove(deviceId, out var removedDevice))
            {
                Log.Information("[-] Removed device: {Device} (disc)", removedDevice);
                tele.UpdateDevicesCount(state.Devices.Count);
                globalEventDispatcher.OnDeviceRemoved(removedDevice.Id);
            }
            else
            {
                Log.Debug("Device not found for deletion: {DiscoveryId}", arg.DiscoveryId);
            }
            return;
        }

        var autoDiscover = arg.AutoDiscover;
        if (autoDiscover.Component != "device_tracker")
        {
            Log.Debug("Ignoring, component isn't device_tracker (" + autoDiscover.Component + ")");
            return;
        }
        var discoveredId = arg.DeviceId ?? autoDiscover.Message?.StateTopic?.Split("/").Last();
        if (discoveredId == null) return;
        bool isNode = discoveredId.StartsWith("node:");
        if (isNode) return;

        state.Devices.GetOrAdd(discoveredId, id =>
        {
            var d = new Device(id, autoDiscover.DiscoveryId, TimeSpan.FromSeconds(state.Config?.Timeout ?? 30)) { Name = autoDiscover.Message?.Name, Track = true, Check = true, LastCalculated = DateTime.UtcNow };
            d.KalmanFilter.Settings = state.KalmanSettings;
            foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
            Log.Information("[+] Track: {Device} (disc)", d);
            return d;
        });
    }

    private string? FindDeviceIdByDiscoveryId(string discoveryId)
    {
        return state.Devices.Values
            .FirstOrDefault(d => d.HassAutoDiscovery.Any(ad => ad.Component == "device_tracker" && string.Equals(ad.DiscoveryId, discoveryId, StringComparison.OrdinalIgnoreCase)))
            ?.Id;
    }

    /// <summary>
    /// Records that we are about to clear the retained discovery config for <paramref name="discoveryId"/>, so the
    /// echo of that clear is not treated as an external delete. Stale entries are pruned on every insert.
    /// </summary>
    internal void RememberSelfClear(string discoveryId)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _selfClearedDiscoveryIds)
        {
            if (now - entry.Value > SelfClearTtl)
                _selfClearedDiscoveryIds.TryRemove(entry.Key, out _);
        }
        _selfClearedDiscoveryIds[discoveryId] = now;
    }

    /// <summary>
    /// Handles a device (or node-as-transmitter) measurement from a node and marks the device for processing.
    /// </summary>
    internal Task OnDeviceMessageReceivedAsync(DeviceMessageEventArgs arg)
    {
        bool isNode = arg.DeviceId.StartsWith("node:");

        var rx = state.Nodes.GetOrAdd(arg.NodeId, id =>
        {
            if (tele.AddUnknownNode(id))
                Log.Warning("Unknown node {nodeId}", id);
            return new Node(id, NodeSourceType.Discovered);
        });

        if (isNode && state.Nodes.TryGetValue(arg.DeviceId.Substring(5), out var tx))
        {
            rx.Nodes.GetOrAdd(tx.Id, f => new NodeToNode(tx, rx)).ReadMessage(arg.Payload);
            if (tx is { HasLocation: true, Stationary: true })
            {
                if (rx is { HasLocation: true, Stationary: true }) // both nodes are stationary
                    tx.RxNodes.GetOrAdd(arg.NodeId, f => new RxNode { Tx = tx, Rx = rx }).ReadMessage(arg.Payload);
            }
            else isNode = false; // if transmitter is not stationary, treat it as a device
        }
        else isNode = false; // if transmitter is not configured, treat it as a device

        if (!isNode)
        {
            if (rx.HasLocation)
            {
                tele.IncrementMessages();
                var device = state.Devices.GetOrAdd(arg.DeviceId, id =>
                {
                    var d = new Device(id, null, TimeSpan.FromSeconds(state.Config?.Timeout ?? 30)) { Check = true };
                    d.KalmanFilter.Settings = state.KalmanSettings;
                    foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
                    return d;
                });
                tele.UpdateDevicesCount(state.Devices.Count);
                var moved = device.Nodes.GetOrAdd(arg.NodeId, f => new DeviceToNode(device, rx)).ReadMessage(arg.Payload);
                if (moved) tele.IncrementMoved();
                _toProcess.Mark(device.Id);
            }
            else
            {
                tele.IncrementSkipped();
            }
        }
        return Task.CompletedTask;
    }

    private async Task ProcessDevicesAsync(CancellationToken stoppingToken)
    {
        await foreach (var deviceId in _toProcess.ReadAllAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (!state.Devices.TryGetValue(deviceId, out var device)) continue; // removed while pending
            var trackChanged = await CheckDeviceAsync(device);
            if (device.Track)
                MarkForLocate(device.Id);
            else
                globalEventDispatcher.OnDeviceChanged(device, trackChanged);
        }
    }

    private async Task CheckIdleDevicesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            MarkIdleDevices(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Queues every tracked device that has never been located, or was last located more than its timeout ago,
    /// so it gets re-evaluated (and, if still tracked, re-located) even when no node is reporting it.
    /// </summary>
    internal void MarkIdleDevices(DateTime now)
    {
        foreach (var device in state.Devices.Values)
        {
            if (device is { Track: true, Confidence: > 0 or null }
                && (device.LastCalculated is null || now - device.LastCalculated.Value > device.Timeout))
            {
                _toProcess.Mark(device.Id);
            }
        }
    }

    private async Task<bool> CheckDeviceAsync(Device device)
    {
        var wasTracked = device.Track;
        var settings = deviceSettingsStore.Get(device.Id);
        if ((settings?.HasAnchor ?? false) && !device.IsAnchored)
        {
            // Apply anchor settings to device
            deviceSettingsStore.ApplyToDevice(device.Id, settings);

            // Only take the fast-path if device actually has a valid anchor after applying
            if (device.IsAnchored)
            {
                device.Track = true;
                device.Check = false;
                if (!wasTracked)
                {
                    tele.UpdateTrackedDevices(state.Devices.Values.Count(a => a.Track));
                    Log.Information("[+] Track {Device} (anchored)", device);
                    foreach (var ad in device.HassAutoDiscovery)
                        await ad.Send(mqtt);
                    return true;
                }
                return false;
            }
            else
            {
                Log.Warning("Anchor failed to apply for device {DeviceId} - falling back to normal tracking", device.Id);
            }
            // If anchor failed to apply, fall through to normal tracking logic
        }

        if (device.Check)
        {
            Log.Debug("Checking {Device}", device);
            device.Track = state.ShouldTrack(device);
            device.Check = false;
        }
        if (device.Track != wasTracked)
        {
            tele.UpdateTrackedDevices(state.Devices.Values.Count(a => a.Track));
            if (device.Track)
            {
                Log.Information("[+] Track {Device}", device);
                foreach (var ad in device.HassAutoDiscovery)
                    await ad.Send(mqtt);
            }
            else
            {
                Log.Information("[-] Track {Device}", device);
                foreach (var ad in device.HassAutoDiscovery)
                {
                    RememberSelfClear(ad.DiscoveryId);
                    await ad.Delete(mqtt);
                }
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Yields each device that needs locating, at most once per pass regardless of how many nodes reported it.
    /// Devices are looked up by id when dequeued; a device removed while pending is skipped.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<Device> GetConsumingEnumerable([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var deviceId in _toLocate.ReadAllAsync(cancellationToken))
        {
            if (state.Devices.TryGetValue(deviceId, out var device))
                yield return device;
        }
    }

    /// <summary>
    /// Discards every device queued for locating and returns how many were dropped. Used by the locator when it
    /// re-acquires the lease so a backlog accumulated while another instance was locating is not replayed.
    /// </summary>
    public int ClearLocateBacklog() => _toLocate.Clear();

    /// <summary>Number of devices currently queued for locating.</summary>
    internal int PendingLocateCount => _toLocate.Count;

    /// <summary>Number of devices currently queued for processing.</summary>
    internal int PendingProcessCount => _toProcess.Count;

    internal void MarkForLocate(string deviceId) => _toLocate.Mark(deviceId);
}
