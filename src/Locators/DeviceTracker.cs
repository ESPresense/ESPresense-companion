using ConcurrentCollections;
using ESPresense.Models;
using ESPresense.Services;
using Serilog;

namespace ESPresense.Locators;

public class DeviceTracker(State state, MqttCoordinator mqtt, TelemetryService tele) : BackgroundService
{

    private readonly ConcurrentHashSet<Device> _dirty = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.MqttMessageMalformed += (s, e) => { tele.IncrementMalformedMessages(); };

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
                        var d = new Device(id, TimeSpan.FromSeconds(state.Config?.Timeout ?? 30)) { Check = true };
                        foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
                        return d;
                    });
                    tele.UpdateDevicesCount(state.Devices.Count);
                    var moved = device.Nodes.GetOrAdd(arg.NodeId, f => new DeviceToNode(device, rx)).ReadMessage(arg.Payload);
                    if (moved) tele.IncrementMoved();

                    if (device.Check)
                    {
                        if (state.ConfigDeviceById.TryGetValue(arg.DeviceId, out var cdById))
                        {
                            device.Track = true;
                            if (!string.IsNullOrWhiteSpace(cdById.Name))
                                device.Name = cdById.Name;
                        }
                        else if (!string.IsNullOrWhiteSpace(device.Name) && state.ConfigDeviceByName.TryGetValue(device.Name, out _))
                            device.Track = true;
                        else if (!string.IsNullOrWhiteSpace(device.Id) && state.IdsToTrack.Any(a => a.IsMatch(device.Id)))
                            device.Track = true;
                        else if (!string.IsNullOrWhiteSpace(device.Name) && state.NamesToTrack.Any(a => a.IsMatch(device.Name)))
                            device.Track = true;
                        else
                            device.Track = false;

                        device.Check = false;
                        tele.UpdateTrackedDevices(state.Devices.Values.Count(a => a.Track));
                    }

                    if (device.Track)
                        foreach (var ad in device.HassAutoDiscovery)
                            await ad.Send(mqtt);

                    if (device.Track && moved)
                        lock (_dirty)
                            _dirty.Add(device);
                }
                else
                {
                    tele.IncrementSkipped();
                }
            }
        };

        await Task.Delay(-1, stoppingToken);
    }

    public Device[] GetMovedDevices()
    {
        lock (_dirty)
        {
            var items = _dirty.ToArray();
            _dirty.Clear();
            return items;
        }
    }
}