using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Locators;

public class MultiScenarioLocator(DeviceTracker dl, State state, MqttCoordinator mqtt, GlobalEventDispatcher globalEventDispatcher, DeviceHistoryStore deviceHistory) : BackgroundService
{
    private const int ConfidenceThreshold = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var todo = dl.GetMovedDevices().Union(state.GetIdleDevices()).ToArray();
            if (todo.Length == 0) await Task.Delay(500, stoppingToken);

            var gps = state.Config?.Gps;

            foreach (var device in todo)
            {
                device.LastCalculated = DateTime.UtcNow;
                var moved = device.Scenarios.AsParallel().Count(s => s.Locate());
                var bs = device.Scenarios.Select((scenario, i) => new { scenario, i }).Where(a => a.scenario.Current).OrderByDescending(a => a.scenario.Confidence).ThenBy(a => a.i).FirstOrDefault()?.scenario;
                if (device.BestScenario == null || bs == null || bs.Confidence - device.BestScenario.Confidence > ConfidenceThreshold)
                    device.BestScenario = bs;
                else
                    bs = device.BestScenario;
                var newState = bs?.Room?.Name ?? bs?.Floor?.Name ?? "not_home";

                if (newState != device.ReportedState)
                {
                    moved += 1;
                    await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", newState);
                    device.ReportedState = newState;
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

                    globalEventDispatcher.OnDeviceChanged(device);
                    if (state?.Config?.History?.Enabled ?? false)
                    {
                        foreach (var ds in device.Scenarios)
                        {
                            if (ds.Confidence == 0) continue;
                            await deviceHistory.Add(new DeviceHistory { Id = device.Id, When = DateTime.UtcNow, X = ds.Location.X, Y = ds.Location.Y, Z = ds.Location.Z, Confidence = ds.Confidence ?? 0, Fixes = ds.Fixes ?? 0, Scenario = ds.Name, Best = ds == device.BestScenario });
                        }
                    }
                }
            }
        }
    }
}