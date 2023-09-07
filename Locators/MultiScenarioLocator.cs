using ConcurrentCollections;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Locators;

internal class MultiScenarioLocator : BackgroundService
{
    private const int ConfidenceThreshold = 2;

    private readonly DatabaseFactory _databaseFactory;
    private readonly MqttConnectionFactory _mqttConnectionFactory;
    private readonly State _state;
    private readonly Telemetry _telemetry = new();

    private ConcurrentHashSet<Device> _dirty = new();

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

        await mc.SubscribeAsync("espresense/devices/+/+");

        mc.ApplicationMessageReceivedAsync += async arg =>
        {
            var parts = arg.ApplicationMessage.Topic.Split('/');

            if (parts is not ["espresense", "devices", _, _])
            {
                _telemetry.Malformed++;
                return;
            }

            var deviceId = parts[2];
            var nodeId = parts[3];
            bool isNode = deviceId.StartsWith("node:");

            if (!_state.Nodes.TryGetValue(nodeId, out var rx))
            {
                _state.Nodes[nodeId] = rx = new Node(nodeId);
                if (_telemetry.UnknownNodes.Add(nodeId))
                    Log.Warning("Unknown node {nodeId}", nodeId);
            }

            if (isNode && _state.Nodes.TryGetValue(deviceId.Substring(5), out var tx))
            {
                if (tx is { HasLocation: true, Stationary: true })
                {
                    if (rx is { HasLocation: true, Stationary: true }) // both nodes are stationary
                        tx.RxNodes.GetOrAdd(nodeId, new RxNode { Tx = tx, Rx = rx }).ReadMessage(arg.ApplicationMessage.PayloadSegment);
                }
                else isNode = false; // if transmitter is not stationary, treat it as a device
            } else isNode = false; // if transmitter is not configured, treat it as a device

            if (!isNode)
            {
                if (rx.HasLocation)
                {
                    _telemetry.Messages++;
                    var device = _state.Devices.GetOrAdd(deviceId, id =>
                    {
                        var d = new Device(id) { Check = true };
                        foreach (var scenario in GetScenarios(d)) d.Scenarios.Add(scenario);
                        return d;
                    });
                    _telemetry.Devices = _state.Devices.Count;
                    var dirty = device.Nodes.GetOrAdd(nodeId, new DeviceNode { Device = device, Node = rx }).ReadMessage(arg.ApplicationMessage.PayloadSegment);
                    if (dirty) _telemetry.Moved++;

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

                        _telemetry.Tracked = _state.Devices.Values.Count(a => a.Track);
                    }

                    if (device.Track)
                        foreach (var ad in device.HassAutoDiscovery)
                            await ad.Send(mc);

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
                await mc.EnqueueAsync("espresense/companion/telemetry", JsonConvert.SerializeObject(_telemetry, SerializerSettings.NullIgnore));
            }

            var todo = _dirty;
            _dirty = new ConcurrentHashSet<Device>();

            var now = DateTime.UtcNow;
            var idleTimeout = TimeSpan.FromSeconds(_state.Config?.Timeout ?? 30);

            foreach (var idle in _state.Devices.Values.Where(a => a is { Track: true, Confidence: > 0 } && now - a.LastCalculated > idleTimeout)) todo.Add(idle);

            var gps = _state.Config?.Gps;

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
                    await mc.EnqueueAsync($"espresense/companion/{device.Id}", state);
                    device.ReportedState = state;
                }

                if (moved > 0)
                {
                    device.ReportedLocation = bs?.Location ?? new Point3D();

                    var (latitude, longitude) = GpsUtil.Add(bs?.Location.X, bs?.Location.Y, gps?.Latitude, gps?.Longitude);

                    if (latitude == null || longitude == null)
                        await mc.EnqueueAsync($"espresense/companion/{device.Id}/attributes",
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
                        await mc.EnqueueAsync($"espresense/companion/{device.Id}/attributes",
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

    private IEnumerable<Scenario> GetScenarios(Device device)
    {
        foreach (var floor in _state.Floors.Values) yield return new Scenario(_state.Config, new NelderMeadMultilateralizer(device, floor, _state), floor.Name);
        //yield return new Scenario(_state.Config, new MultiFloorMultilateralizer(device, _state), "Multifloor");
        yield return new Scenario(_state.Config, new NearestNode(device), "NearestNode");
    }
}