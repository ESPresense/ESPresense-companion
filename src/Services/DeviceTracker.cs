using System.Threading.Channels;
using ESPresense.Models;
using Serilog;

namespace ESPresense.Services;

public class DeviceTracker(State state, MqttCoordinator mqtt, TelemetryService tele) : BackgroundService
{
    private readonly Channel<Device> _toProcessChannel = Channel.CreateUnbounded<Device>();
    private readonly Channel<Device> _toLocateChannel = Channel.CreateUnbounded<Device>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.MqttMessageMalformed += (s, e) => { tele.IncrementMalformedMessages(); };

        mqtt.PreviousDeviceDiscovered += (s, arg) =>
        {
            if (arg.AutoDiscover.Component != "device_tracker")
            {
                Log.Debug("Ignoring, component isn't device_tracker (" + arg.AutoDiscover.Component + ")");
                return;
            }
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

        mqtt.DeviceMessageReceivedAsync += async arg =>
        {
            bool isNode = arg.DeviceId.StartsWith("node:");

            if (!state.Nodes.TryGetValue(arg.NodeId, out var rx))
            {
                state.Nodes[arg.NodeId] = rx = new Node(arg.NodeId);
                if (tele.AddUnknownNode(arg.NodeId))
                    Log.Warning("Unknown node {nodeId}", arg.NodeId);
            }

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
            await CheckDeviceAsync(device);
            if (device.Track)
                await _toLocateChannel.Writer.WriteAsync(device, stoppingToken);
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

    private bool ShouldTrackDevice(Device device)
    {
        if (state.ConfigDeviceById.TryGetValue(device.Id, out var cdById))
        {
            if (!string.IsNullOrWhiteSpace(cdById.Name))
                device.Name = cdById.Name;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(device.Name) && state.ConfigDeviceByName.TryGetValue(device.Name, out _))
            return true;
        if (!string.IsNullOrWhiteSpace(device.Id) && state.IdsToTrack.Any(a => a.IsMatch(device.Id)))
            return true;
        if (!string.IsNullOrWhiteSpace(device.Name) && state.NamesToTrack.Any(a => a.IsMatch(device.Name)))
            return true;
        return false;
    }

    private async Task CheckDeviceAsync(Device device)
    {
        var wasTracked = device.Track;
        if (device.Check)
        {
            Log.Debug("Checking {Device}", device);
            device.Track = ShouldTrackDevice(device);
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
        }
    }

    public IAsyncEnumerable<Device> GetConsumingEnumerable(CancellationToken cancellationToken)
    {
        return _toLocateChannel.Reader.ReadAllAsync(cancellationToken);
    }
}
