using System.Threading.Channels;
using ESPresense.Controllers;
using ESPresense.Models;
using Serilog;

namespace ESPresense.Services;

public class DeviceTracker(State state, IMqttCoordinator mqtt, TelemetryService tele, GlobalEventDispatcher globalEventDispatcher, DeviceSettingsStore deviceSettingsStore) : BackgroundService
{
    private readonly Channel<Device> _toProcessChannel = Channel.CreateUnbounded<Device>();
    private readonly Channel<Device> _toLocateChannel = Channel.CreateUnbounded<Device>();

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
    /// - Starts and awaits two long-running background tasks: ProcessDevicesAsync and CheckIdleDevicesAsync, which consume internal channels to evaluate and locate devices.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.DeviceMessageReceivedAsync += (e) =>
        {
            globalEventDispatcher.OnDeviceMessageReceived(e);
            return Task.CompletedTask;
        };

        mqtt.MqttMessageMalformed += (s, e) => { tele.IncrementMalformedMessages(); };

        // Handle device discovery
        mqtt.PreviousDeviceDiscovered += (s, arg) =>
        {
            if (arg.AutoDiscover == null)
            {
                // Handle null AutoDiscover (message deletion)
                var deleteDeviceId = arg.AutoDiscover?.Message?.StateTopic?.Split('/').Last();
                if (deleteDeviceId != null && state.Devices.TryRemove(deleteDeviceId, out var removedDevice))
                {
                    Log.Debug("[-] Removed device: {Device} (disc)", removedDevice);
                }
                else
                {
                    Log.Debug("Device not found for deletion: {DeviceId}", deleteDeviceId);
                }
                return;
            }

            if (arg.AutoDiscover.Component != "device_tracker")
            {
                Log.Debug("Ignoring, component isn't device_tracker (" + arg.AutoDiscover.Component + ")");
                return;
            }
            if (arg.AutoDiscover.Message?.StateTopic == null) return;
            var deviceId = arg.AutoDiscover.Message.StateTopic.Split("/").Last();
            bool isNode = deviceId.StartsWith("node:");
            if (isNode) return;

            var device = state.Devices.GetOrAdd(deviceId, id =>
            {
                var d = new Device(id, arg.AutoDiscover.DiscoveryId, TimeSpan.FromSeconds(state.Config?.Timeout ?? 30)) { Name = arg.AutoDiscover.Message.Name, Track = true, Check = true, LastCalculated = DateTime.UtcNow };
                foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
                Log.Information("[+] Track: {Device} (disc)", d);
                return d;
            });
        };

        // Handle device messages
        mqtt.DeviceMessageReceivedAsync += async arg =>
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
                        foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
                        return d;
                    });
                    tele.UpdateDevicesCount(state.Devices.Count);
                    var moved = device.Nodes.GetOrAdd(arg.NodeId, f => new DeviceToNode(device, rx)).ReadMessage(arg.Payload);
                    if (moved) tele.IncrementMoved();
                    await _toProcessChannel.Writer.WriteAsync(device, stoppingToken);
                }
                else
                {
                    tele.IncrementSkipped();
                }
            }
        };

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

    private async Task ProcessDevicesAsync(CancellationToken stoppingToken)
    {
        await foreach (var device in _toProcessChannel.Reader.ReadAllAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) break;
            var trackChanged = await CheckDeviceAsync(device);
            if (device.Track)
                await _toLocateChannel.Writer.WriteAsync(device, stoppingToken);
            else
                globalEventDispatcher.OnDeviceChanged(device, trackChanged);
        }
    }

    private async Task CheckIdleDevicesAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            var now = DateTime.UtcNow;
            foreach (var device in state.Devices.Values)
            {
                if (device is { Track: true, Confidence: > 0 or null } && now - device.LastCalculated > device.Timeout)
                {
                    await _toProcessChannel.Writer.WriteAsync(device, stoppingToken);
                }
            }
        }
    }

    private async Task<bool> CheckDeviceAsync(Device device)
    {
        var wasTracked = device.Track;
        var settings = deviceSettingsStore.Get(device.Id);
        if (settings?.HasAnchor ?? false)
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
                    await ad.Delete(mqtt);
            }
            return true;
        }
        return false;
    }

    public IAsyncEnumerable<Device> GetConsumingEnumerable(CancellationToken cancellationToken)
    {
        return _toLocateChannel.Reader.ReadAllAsync(cancellationToken);
    }
}
