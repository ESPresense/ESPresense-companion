using ConcurrentCollections;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Locators;

internal class MultiScenarioLocator(State state, MqttCoordinator mqtt, DatabaseFactory databaseFactory) : BackgroundService
{
    private const int ConfidenceThreshold = 2;

    private readonly Telemetry _telemetry = new();

    private ConcurrentHashSet<Device> _dirty = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dh = await databaseFactory.GetDeviceHistory();

        mqtt.MqttMessageMalformed += (s,e) =>
        {
            _telemetry.Malformed++;
        };

        mqtt.DeviceMessageReceivedAsync += async arg =>
        {
            bool isNode = arg.DeviceId.StartsWith("node:");

            if (!state.Nodes.TryGetValue(arg.NodeId, out var rx))
            {
                state.Nodes[arg.NodeId] = rx = new Node(arg.NodeId);
                if (_telemetry.UnknownNodes.Add(arg.NodeId))
                    Log.Warning("Unknown node {nodeId}", arg.NodeId);
            }

            if (isNode && state.Nodes.TryGetValue(arg.DeviceId.Substring(5), out var tx))
            {
                if (tx is { HasLocation: true, Stationary: true })
                {
                    if (rx is { HasLocation: true, Stationary: true }) // both nodes are stationary
                        tx.RxNodes.GetOrAdd(arg.NodeId, new RxNode { Tx = tx, Rx = rx }).ReadMessage(arg.Payload);
                }
                else isNode = false; // if transmitter is not stationary, treat it as a device
            } else isNode = false; // if transmitter is not configured, treat it as a device

            if (!isNode)
            {
                if (rx.HasLocation)
                {
                    _telemetry.Messages++;
                    var device = state.Devices.GetOrAdd(arg.DeviceId, id =>
                    {
                        var d = new Device(id) { Check = true };
                        foreach (var scenario in state.GetScenarios(d)) d.Scenarios.Add(scenario);
                        return d;
                    });
                    _telemetry.Devices = state.Devices.Count;
                    var dirty = device.Nodes.GetOrAdd(arg.NodeId, new DeviceNode { Device = device, Node = rx }).ReadMessage(arg.Payload);
                    if (dirty) _telemetry.Moved++;

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

                        _telemetry.Tracked = state.Devices.Values.Count(a => a.Track);
                    }

                    if (device.Track)
                        foreach (var ad in device.HassAutoDiscovery)
                            await ad.Send(mqtt);

                    if (device.Track && dirty)
                        _dirty.Add(device);
                }
                else
                {
                    _telemetry.Skipped++;
                }
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
                await mqtt.EnqueueAsync("espresense/companion/telemetry", JsonConvert.SerializeObject(_telemetry, SerializerSettings.NullIgnore));
            }

            var todo = _dirty;
            _dirty = new ConcurrentHashSet<Device>();

            var now = DateTime.UtcNow;
            var idleTimeout = TimeSpan.FromSeconds(state.Config?.Timeout ?? 30);

            foreach (var idle in state.Devices.Values.Where(a => a is { Track: true, Confidence: > 0 } && now - a.LastCalculated > idleTimeout)) todo.Add(idle);

            var gps = state.Config?.Gps;

            foreach (var device in todo)
            {
                device.LastCalculated = now;
                var moved = device.Scenarios.AsParallel().Count(s => s.Locate());
                var bs = device.Scenarios.Select((scenario, i) => new { scenario, i }).Where(a => a.scenario.Current).OrderByDescending(a => a.scenario.Confidence).ThenBy(a => a.i).FirstOrDefault()?.scenario;
                if (device.BestScenario == null || bs == null || bs.Confidence - device.BestScenario.Confidence > ConfidenceThreshold)
                    device.BestScenario = bs;
                else
                    bs = device.BestScenario;
                var state = bs?.Room?.Name ?? bs?.Floor?.Name ?? "not_home";

                if (state != device.ReportedState)
                {
                    moved += 1;
                    await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", state);
                    device.ReportedState = state;
                }

                if (moved > 0)
                {
                    device.ReportedLocation = bs?.Location ?? new Point3D();

                    var (latitude, longitude) = GpsUtil.Add(bs?.Location.X, bs?.Location.Y, gps?.Latitude, gps?.Longitude);

                    if (latitude == null || longitude == null)
                        await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes",
                            JsonConvert.SerializeObject(new
                            {
                                x = bs?.Location.X,
                                y = bs?.Location.Y,
                                z = bs?.Location.Z,
                                confidence = bs?.Confidence,
                                fixes = bs?.Fixes,
                                best_scenario = bs?.Name
                            }, SerializerSettings.NullIgnore)
                        );
                    else
                        await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes",
                            JsonConvert.SerializeObject(new
                            {
                                source_type = "espresense",
                                latitude,
                                longitude,
                                elevation = bs?.Location.Z + gps?.Elevation,
                                x = bs?.Location.X,
                                y = bs?.Location.Y,
                                z = bs?.Location.Z,
                                confidence = bs?.Confidence,
                                fixes = bs?.Fixes,
                                best_scenario = bs?.Name
                            }, SerializerSettings.NullIgnore)
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
}