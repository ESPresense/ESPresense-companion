using ConcurrentCollections;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet.Extensions.ManagedClient;
using Serilog;

namespace ESPresense.Locators;

internal class MultiScenarioLocator : BackgroundService
{
    private readonly MqttConnectionFactory _mqttConnectionFactory;
    private readonly State _state;

    private ConcurrentHashSet<Device> _dirty = new();
    private readonly DatabaseFactory _databaseFactory;

    public MultiScenarioLocator(State state, MqttConnectionFactory mqttConnectionFactory, DatabaseFactory databaseFactory)
    {
        _state = state;
        _mqttConnectionFactory = mqttConnectionFactory;
        _databaseFactory = databaseFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dh = await _databaseFactory.GetDeviceHistory();
        var mc = await _mqttConnectionFactory.GetClient(true);

        await mc.SubscribeAsync("espresense/devices/#");

        mc.ApplicationMessageReceivedAsync += arg =>
        {
            var parts = arg.ApplicationMessage.Topic.Split('/');

            var deviceId = parts[2];
            var nodeId = parts[3];

            if (_state.Nodes.TryGetValue(nodeId, out var node))
            {
                var device = _state.Devices.GetOrAdd(deviceId, a =>
                {
                    var d = new Device { Id = a, Check = true };
                    foreach (var scenario in GetScenarios(d)) d.Scenarios.Add(scenario);
                    return d;
                });
                var dirty = device.Nodes.GetOrAdd(nodeId, new DeviceNode { Device = device, Node = node }).ReadMessage(arg.ApplicationMessage.Payload);

                if (device.Check)
                {
                    if (_state.ConfigDeviceById.TryGetValue(deviceId, out var cdById))
                    {
                        device.Track = cdById.Track;
                        if (!string.IsNullOrWhiteSpace(cdById.Name))
                            device.Name = cdById.Name;
                    }
                    else if (!string.IsNullOrWhiteSpace(device.Name) && _state.ConfigDeviceByName.TryGetValue(device.Name, out var cdByName))
                        device.Track = cdByName.Track;
                    else if (_state.ConfigDeviceById.TryGetValue("*", out var cdByIdWild))
                        device.Track = cdByIdWild.Track;

                    if (!string.IsNullOrWhiteSpace(device.Name) && _state.ConfigDeviceByName.TryGetValue("*", out var cdByNameWild))
                        device.Track = cdByNameWild.Track;
                    device.Check = false;
                }

                if (device.Track && dirty)
                    _dirty.Add(device);
            }
            else Log.Warning("Unknown node {nodeId}", nodeId);

            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_dirty.IsEmpty)
                await Task.Delay(500, stoppingToken);

            var todo = _dirty;
            _dirty = new ConcurrentHashSet<Device>();

            var now = DateTime.UtcNow;
            var idleTimeout = TimeSpan.FromSeconds(_state.Config?.Timeout ?? 30);

            foreach (var idle in _state.Devices.Values.Where(a => a is { Track: true, Confidence: > 0 } && now - a.LastCalculated > idleTimeout)) todo.Add(idle);

            foreach (var device in todo.Where(d => d.Scenarios.AsParallel().Count(s => s.Locate()) > 0))
            {
                var bs = device.BestScenario = device.Scenarios.Select((scenario, i) => new { scenario, i }).OrderByDescending(a => a.scenario.Confidence).ThenBy(a => a.i).First().scenario;
                await mc.EnqueueAsync("espresense/ips/" + device.Id, $"{{ \"x\":{bs.Location.X}, \"y\":{bs.Location.Y}, \"z\":{bs.Location.Z}, \"name\":\"{device.Name ?? device.Id}\", \"confidence\":\"{bs.Confidence}\", \"fixes\":\"{bs.Fixes}\", \"scenario\":\"{bs.Name}\" }}");
                foreach (var ds in device.Scenarios)
                {
                    if (ds.Confidence == 0) continue;
                    await dh.Add(new DeviceHistory { Id = device.Id, When = DateTime.UtcNow, X = ds.Location.X, Y = ds.Location.Y, Z = ds.Location.Z, Confidence = ds.Confidence ?? 0, Fixes = ds.Fixes ?? 0, Scenario = ds.Name, Best = ds == bs });
                }

                device.ReportedLocation = bs.Location;
                device.ReportedRoom = bs.Room;
                device.LastCalculated = now;
            }
        }
    }

    private IEnumerable<Scenario> GetScenarios(Device device)
    {
        foreach (var floor in _state.Floors.Values) yield return new Scenario(new NelderMeadMultilateralizer(device, floor), floor.Name);
        yield return new Scenario(new NearestNode(device), "NearestNode");
    }
}