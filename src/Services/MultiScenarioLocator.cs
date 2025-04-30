using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using ESPresense.Extensions;

namespace ESPresense.Services;

/// <summary>
/// Background service that, each time a <see cref="Device"/> surfaces from the
/// tracker pipeline, does **three** things:
///   1. Picks the scenario with the **highest raw Confidence** and feeds that
///      measurement into the device‑level Kalman filter.
///   2. Computes a motion‑consistency‑weighted confidence for every scenario
///      (Gaussian penalty against the Kalman predicted location) and smooths
///      per‑scenario probabilities over time.
///   3. Publishes the scenario with the *highest smoothed probability* (ties
///      broken by confidence) to MQTT + Home Assistant attributes.
/// </summary>
public class MultiScenarioLocator(DeviceTracker dl,
                                   State state,
                                   MqttCoordinator mqtt,
                                   GlobalEventDispatcher globalEventDispatcher,
                                   DeviceHistoryStore deviceHistory) : BackgroundService
{
    private const double PriorWeight     = 0.7;  // temporal smoothing
    private const double NewDataWeight   = 0.3;
    private const double MotionSigma     = 2.0;  // metres, for Gaussian weight

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
        {
            // -----------------------------------------------------------------
            // 1. Refresh all scenarios -------------------------------------------------
            // -----------------------------------------------------------------
            device.LastCalculated = DateTime.UtcNow;
            var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

            // -----------------------------------------------------------------
            // 2. Feed Kalman with the scenario that has the highest raw Confidence
            // -----------------------------------------------------------------
            var kalmanSource = device.Scenarios
                .Where(s => s.Current)
                .OrderByDescending(s => s.Confidence)
                .FirstOrDefault();

            if (kalmanSource != null)
            {
                device.UpdateLocation(kalmanSource.Location, kalmanSource.Confidence ?? 0);
            }

            // Predicted location from the updated Kalman filter (x̂ₖ|ₖ⁻¹)
            var (predictedLocation, _) = device.KalmanFilter.GetPrediction();

            // -----------------------------------------------------------------
            // 3. Motion‑consistency weighting for every scenario -------------------
            // -----------------------------------------------------------------
            foreach (var scenario in device.Scenarios)
            {
                if (!scenario.Current)
                {
                    scenario.WeightedConfidence = 0;
                    continue;
                }

                double delta = predictedLocation.DistanceTo(scenario.Location);
                double mcw   = Math.Exp(-(delta * delta) / (2 * MotionSigma * MotionSigma));
                scenario.WeightedConfidence = (scenario.Confidence ?? 0) * mcw;
            }

            // -----------------------------------------------------------------
            // 4. Inline probability smoothing (was UpdateScenarioProbabilities)
            // -----------------------------------------------------------------
            double totalWeightedConfidence = device.Scenarios.Sum(s => s.WeightedConfidence);

            if (totalWeightedConfidence > 0)
            {
                foreach (var scenario in device.Scenarios)
                {
                    double newProb = scenario.WeightedConfidence / totalWeightedConfidence;
                    scenario.Probability = PriorWeight * scenario.Probability + NewDataWeight * newProb;
                }

                // normalise
                double sumProb = device.Scenarios.Sum(s => s.Probability);
                if (sumProb > 0)
                {
                    foreach (var scenario in device.Scenarios)
                        scenario.Probability /= sumProb;
                }
            }

            // -----------------------------------------------------------------
            // 5. Choose scenario to *report* (was SelectBestScenario)
            // -----------------------------------------------------------------
            var bestScenario = device.Scenarios
                .Where(s => s.Current)
                .OrderByDescending(s => s.Probability)
                .ThenByDescending(s => s.Confidence)
                .ThenBy(s => device.Scenarios.IndexOf(s))
                .FirstOrDefault();

            device.BestScenario = bestScenario;

            // -----------------------------------------------------------------
            // 6. Publish state / attributes if anything moved ----------------------
            // -----------------------------------------------------------------
            if (bestScenario != null)
            {
                var newState = device.Room?.Name ?? device.Floor?.Name ?? "not_home";
                if (newState != device.ReportedState)
                {
                    moved += 1;
                    await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", newState);
                    device.ReportedState = newState;
                }
            }

            if (moved > 0 && bestScenario != null)
            {
                device.ReportedLocation = device.Location ?? new Point3D();

                var gps      = state?.Config?.Gps;
                var location = device.Location ?? new Point3D();
                // Use extension method syntax again, now that the namespace is imported
                var (lat, lon) = gps.Add(location.X, location.Y);

                var payload = JsonConvert.SerializeObject(new
                {
                    source_type   = "espresense",
                    latitude      = lat,
                    longitude     = lon,
                    elevation     = location.Z + gps?.Elevation,
                    x             = location.X,
                    y             = location.Y,
                    z             = location.Z,
                    confidence    = bestScenario.Confidence,
                    fixes         = bestScenario.Fixes,
                    best_scenario = bestScenario.Name,
                    last_seen     = device.LastSeen
                }, SerializerSettings.NullIgnore);

                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true);

                globalEventDispatcher.OnDeviceChanged(device, false);

                // optional history
                if (state?.Config?.History?.Enabled ?? false)
                {
                    foreach (var ds in device.Scenarios.Where(ds => ds.Confidence != 0))
                    {
                        await deviceHistory.Add(new DeviceHistory
                        {
                            Id        = device.Id,
                            When      = DateTime.UtcNow,
                            X         = ds.Location.X,
                            Y         = ds.Location.Y,
                            Z         = ds.Location.Z,
                            Confidence = ds.Confidence ?? 0,
                            Fixes      = ds.Fixes ?? 0,
                            Scenario   = ds.Name,
                            Best       = ds == bestScenario
                        });
                    }
                }
            }
        }
    }
}
