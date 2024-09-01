using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Locators;

public class MultiScenarioLocator(DeviceTracker dl, State state, MqttCoordinator mqtt, GlobalEventDispatcher globalEventDispatcher, DeviceHistoryStore deviceHistory)
    : BackgroundService
{
    private const double PriorWeight = 0.7; // Weight for the prior probability
    private const double NewDataWeight = 0.3; // Weight for the new data

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

                // Update scenario probabilities using Bayesian approach
                UpdateScenarioProbabilities(device);

                // Select the best scenario based on probabilities
                var bs = device.BestScenario = SelectBestScenario(device);

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
                        foreach (var ds in device.Scenarios)
                        {
                            if (ds.Confidence == 0) continue;
                            await deviceHistory.Add(new DeviceHistory { Id = device.Id, When = DateTime.UtcNow, X = ds.Location.X, Y = ds.Location.Y, Z = ds.Location.Z, Confidence = ds.Confidence ?? 0, Fixes = ds.Fixes ?? 0, Scenario = ds.Name, Best = ds == device.BestScenario });
                        }
                }
            }
        }
    }

    private void UpdateScenarioProbabilities(Device device)
    {
        double totalConfidence = device.Scenarios.Sum(s => s.Confidence ?? 0);

        if (totalConfidence > 0)
        {
            foreach (var scenario in device.Scenarios)
            {
                var newProbability = (scenario.Confidence ?? 0) / totalConfidence;
                scenario.Probability = PriorWeight * scenario.Probability + NewDataWeight * newProbability;
            }

            // Normalize probabilities
            var sum = device.Scenarios.Sum(s => s.Probability);
            foreach (var scenario in device.Scenarios)
            {
                scenario.Probability /= sum;
            }
        }
    }

    private Scenario? SelectBestScenario(Device device)
    {
        return device.Scenarios
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s=> s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault(s => s.Current);
    }
}