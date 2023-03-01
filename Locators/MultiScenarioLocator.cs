using ConcurrentCollections;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Serilog;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace ESPresense.Locators;

internal class MultiScenarioLocator : BackgroundService
{
    private readonly DatabaseFactory _databaseFactory;
    private readonly MqttConnectionFactory _mqttConnectionFactory;
    private readonly State _state;

    static JsonSerializerSettings jsj = new() { NullValueHandling = NullValueHandling.Ignore, ContractResolver = new CamelCasePropertyNamesContractResolver() };

    private ConcurrentHashSet<Device> _dirty = new();
    private readonly Telemetry tele = new();

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

        mc.ApplicationMessageReceivedAsync += async arg =>
        {
            var parts = arg.ApplicationMessage.Topic.Split('/', 4);

            if (parts.Length != 4 || parts[0] != "espresense" || parts[1] != "devices")
            {
                tele.Malformed++;
                return;
            }

            var deviceId = parts[2];
            var nodeId = parts[3];

            if (_state.Nodes.TryGetValue(nodeId, out var node))
            {
                tele.Messages++;
                var device = _state.Devices.GetOrAdd(deviceId, id =>
                {
                    var d = new Device(id) { Check = true };
                    foreach (var scenario in GetScenarios(d)) d.Scenarios.Add(scenario);
                    return d;
                });
                tele.Devices = _state.Devices.Count;
                var dirty = device.Nodes.GetOrAdd(nodeId, new DeviceNode { Device = device, Node = node }).ReadMessage(arg.ApplicationMessage.Payload);
                if (dirty) tele.Moved++;

                if (device.Check)
                {
                    if (_state.ConfigDeviceById.TryGetValue(deviceId, out var cdById))
                    {
                        device.Track = true;
                        if (!string.IsNullOrWhiteSpace(cdById.Name))
                            device.Name = cdById.Name;
                    }
                    else if (!string.IsNullOrWhiteSpace(device.Name) && _state.ConfigDeviceByName.TryGetValue(device.Name, out _))
                        device.Track = true;
                    else if (!string.IsNullOrWhiteSpace(device.Id) && _state.IdsToTrack.Any(a => a.IsMatch(device.Id)))
                        device.Track = true;
                    else if (!string.IsNullOrWhiteSpace(device.Name) && _state.NamesToTrack.Any(a => a.IsMatch(device.Name)))
                        device.Track = true;
                    else
                        device.Track = false;

                    device.Check = false;

                    tele.Tracked = _state.Devices.Values.Count(a => a.Track);
                }

                if (device.Track)
                    foreach (var ad in device.HassAutoDiscovery)
                        await ad.Send(mc);

                if (device.Track && dirty)
                    _dirty.Add(device);
            }
            else
            {
                tele.Skipped++;
                if (tele.UnknownNodes.Add(nodeId))
                    Log.Warning("Unknown node {nodeId}", nodeId);
            }
        };

        var telemetryLastSent = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            while (_dirty.IsEmpty)
                await Task.Delay(500, stoppingToken);

            if (DateTime.UtcNow - telemetryLastSent > TimeSpan.FromSeconds(30))
            {
                telemetryLastSent = DateTime.UtcNow;
                await mc.EnqueueAsync("espresense/companion/telemetry", JsonConvert.SerializeObject(tele, jsj));
            }

            var todo = _dirty;
            _dirty = new ConcurrentHashSet<Device>();

            var now = DateTime.UtcNow;
            var idleTimeout = TimeSpan.FromSeconds(_state.Config?.Timeout ?? 30);

            foreach (var idle in _state.Devices.Values.Where(a => a is { Track: true, Confidence: > 0 } && now - a.LastCalculated > idleTimeout)) todo.Add(idle);

            foreach (var device in todo)
            {
                device.LastCalculated = now;
                var moved = device.Scenarios.AsParallel().Count(s => s.Locate());
                var bs = device.BestScenario = device.Scenarios.Select((scenario, i) => new { scenario, i }).Where(a => a.scenario.Current).OrderByDescending(a => a.scenario.Confidence).ThenBy(a => a.i).FirstOrDefault()?.scenario;
                var state = bs?.Room?.Name ?? bs?.Floor?.Name ?? "not_home";

                if (state != device.ReportedState)
                {
                    moved += 1;
                    await mc.EnqueueAsync($"espresense/companion/{device.Id}", state);
                    device.ReportedState = state;
                }

                if (moved > 0)
                {
                    device.ReportedLocation = bs?.Location ?? new Point3D();
                    await mc.EnqueueAsync($"espresense/companion/{device.Id}/attributes",
                        JsonConvert.SerializeObject(new
                        {
                            bs?.Location.X,
                            bs?.Location.Y,
                            bs?.Location.Z,
                            bs?.Confidence,
                            bs?.Fixes,
                            BestScenario = bs?.Name
                        }, jsj)
                    );

                    foreach (var ds in device.Scenarios)
                    {
                        if (ds.Confidence == 0) continue;
                        await dh.Add(new DeviceHistory { Id = device.Id, When = DateTime.UtcNow, X = ds.Location.X, Y = ds.Location.Y, Z = ds.Location.Z, Confidence = ds.Confidence ?? 0, Fixes = ds.Fixes ?? 0, Scenario = ds.Name, Best = ds == bs });
                    }
                }
            }
        }
    }

    private IEnumerable<Scenario> GetScenarios(Device device)
    {
        foreach (var floor in _state.Floors.Values) yield return new Scenario(_state.Config, new NelderMeadMultilateralizer(device, floor), floor.Name);
        yield return new Scenario(_state.Config, new NearestNode(device), "NearestNode");
    }
}