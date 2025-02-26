using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class MultiScenarioLocator(DeviceTracker dl, State state, MqttCoordinator mqtt, GlobalEventDispatcher globalEventDispatcher, DeviceHistoryStore deviceHistory)
    : BackgroundService
{
    private const double PriorWeight = 0.7;
    private const double NewDataWeight = 0.3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
        {
            device.LastCalculated = DateTime.UtcNow;
            var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

            UpdateScenarioProbabilities(device);

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

                var gps = state?.Config?.Gps;
                var (latitude, longitude) = GpsUtil.Add(bs?.Location.X, bs?.Location.Y, gps?.Latitude, gps?.Longitude);
                var payload = JsonConvert.SerializeObject(new
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
                    best_scenario = bs?.Name,
                    last_seen = device.LastSeen
                }, SerializerSettings.NullIgnore);
                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true);

                globalEventDispatcher.OnDeviceChanged(device, false);
                if (state?.Config?.History?.Enabled ?? false)
                {
                    foreach (var ds in device.Scenarios.Where(ds => ds.Confidence != 0))
                    {
                        await deviceHistory.Add(new DeviceHistory
                        {
                            Id = device.Id,
                            When = DateTime.UtcNow,
                            X = ds.Location.X,
                            Y = ds.Location.Y,
                            Z = ds.Location.Z,
                            Confidence = ds.Confidence ?? 0,
                            Fixes = ds.Fixes ?? 0,
                            Scenario = ds.Name,
                            Best = ds == bs
                        });
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
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault(s => s.Current);
    }
}