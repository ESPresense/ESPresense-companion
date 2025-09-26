using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using ESPresense.Extensions;
using System.Globalization;
using System.Text;
using TextExtensions;

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
    private const double PriorWeight       = 0.7;  // temporal smoothing
    private const double NewDataWeight     = 0.3;
    private const double MotionSigma       = 2.0;  // metres, for Gaussian weight
    private const double ProbabilityEpsilon = 0.001;

    internal async Task ProcessDevice(Device device)
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
                device.UpdateLocation(kalmanSource.Location);
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

            var probabilityConfig = state.Config?.BayesianProbabilities;
            Dictionary<string, double>? probabilityAttributes = null;
            bool probabilityChanged = false;

            if (probabilityConfig is { Enabled: true })
            {
                var probabilityVector = BuildProbabilityVector(device, bestScenario);
                probabilityChanged = await PublishProbabilitySensorsAsync(device, probabilityVector, probabilityConfig);

                if (probabilityVector.Count > 0)
                {
                    probabilityAttributes = probabilityVector.ToDictionary(
                        kvp => kvp.Key,
                        kvp => Math.Round(kvp.Value, 4));
                }
            }
            else
            {
                probabilityChanged = await ClearProbabilityOutputsAsync(device);
            }

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
            else if (device.ReportedState != "not_home")
            {
                moved += 1;
                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", "not_home");
                device.ReportedState = "not_home";
            }

            if ((moved > 0 || probabilityChanged) && (bestScenario != null || probabilityAttributes != null))
            {
                var includeLocation = bestScenario != null;

                if (includeLocation)
                {
                    device.ReportedLocation = device.Location ?? new Point3D();
                }

                var gps      = state?.Config?.Gps;
                var location = device.Location ?? new Point3D();

                // Only include GPS coordinates if reporting is enabled and we have a best scenario
                var (lat, lon) = includeLocation && gps?.Report == true ? gps.Add(location.X, location.Y) : (null, null);
                var elevation = includeLocation && gps?.Report == true ? location.Z + gps?.Elevation : null;

                var payload = JsonConvert.SerializeObject(new
                {
                    source_type   = includeLocation ? "espresense" : null,
                    latitude      = includeLocation ? lat : null,
                    longitude     = includeLocation ? lon : null,
                    elevation     = includeLocation ? elevation : null,
                    x             = includeLocation ? (double?)location.X : null,
                    y             = includeLocation ? (double?)location.Y : null,
                    z             = includeLocation ? (double?)location.Z : null,
                    confidence    = bestScenario?.Confidence,
                    fixes         = bestScenario?.Fixes,
                    best_scenario = bestScenario?.Name,
                    last_seen     = device.LastSeen,
                    probabilities = probabilityAttributes
                }, SerializerSettings.NullIgnore);

                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true);

                if (includeLocation)
                {
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

    private Dictionary<string, double> BuildProbabilityVector(Device device, Scenario? bestScenario)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var activeScenarios = device.Scenarios.Where(s => s.Current && s.Probability > 0).ToList();
        if (activeScenarios.Count == 0 || bestScenario == null)
        {
            result["not_home"] = 1;
            return result;
        }

        foreach (var scenario in activeScenarios)
        {
            var key = scenario.Room?.Name ?? scenario.Name ?? scenario.Floor?.Name ?? "unknown";
            if (string.IsNullOrWhiteSpace(key)) key = "unknown";

            result[key] = result.TryGetValue(key, out var existing)
                ? existing + scenario.Probability
                : scenario.Probability;
        }

        var sum = result.Values.Sum();
        if (sum <= 0)
        {
            result.Clear();
            result["other"] = 1;
            return result;
        }

        foreach (var key in result.Keys.ToList())
        {
            var normalized = result[key] / sum;
            result[key] = Math.Clamp(normalized, 0, 1);
        }

        var normalisedSum = result.Values.Sum();
        if (normalisedSum < 1)
        {
            var remainder = Math.Max(0, 1 - normalisedSum);
            if (remainder > 0.0001)
            {
                result["other"] = result.TryGetValue("other", out var other) ? other + remainder : remainder;
            }
        }
        else if (normalisedSum > 1)
        {
            foreach (var key in result.Keys.ToList())
            {
                result[key] /= normalisedSum;
            }
        }

        return result;
    }

    private async Task<bool> PublishProbabilitySensorsAsync(Device device, IReadOnlyDictionary<string, double> probabilities, ConfigBayesianProbabilities config)
    {
        var changed = false;
        var activeKeys = new HashSet<string>(probabilities.Keys, StringComparer.OrdinalIgnoreCase);

        // Check if probabilities have changed significantly
        var probabilitiesChanged = false;
        foreach (var (roomName, probability) in probabilities)
        {
            if (!device.BayesianProbabilities.TryGetValue(roomName, out var existing) || Math.Abs(existing - probability) > ProbabilityEpsilon)
            {
                probabilitiesChanged = true;
                device.BayesianProbabilities[roomName] = probability;
            }
        }

        // Check if any rooms were removed
        foreach (var key in device.BayesianProbabilities.Keys.ToArray())
        {
            if (!activeKeys.Contains(key))
            {
                probabilitiesChanged = true;
                device.BayesianProbabilities.TryRemove(key, out _);
            }
        }

        // Publish single JSON message if probabilities changed
        if (probabilitiesChanged)
        {
            var payload = JsonConvert.SerializeObject(probabilities, SerializerSettings.NullIgnore);
            await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/probabilities", payload, retain: config.Retain);
            changed = true;
        }

        // Manage auto-discovery for individual room sensors
        foreach (var (roomName, probability) in probabilities)
        {
            if (!IsSyntheticRoom(roomName) && probability >= config.DiscoveryThreshold)
            {
                var discovery = device.BayesianDiscoveries.GetOrAdd(roomName, key => CreateProbabilityDiscovery(device, key));
                if (!device.HassAutoDiscovery.Contains(discovery))
                    device.HassAutoDiscovery.Add(discovery);
                await discovery.Send(mqtt);
            }
            else if (device.BayesianDiscoveries.TryRemove(roomName, out var staleDiscovery))
            {
                device.HassAutoDiscovery.Remove(staleDiscovery);
                await staleDiscovery.Delete(mqtt);
                changed = true;
            }
        }

        // Clean up discoveries for removed rooms
        foreach (var key in device.BayesianDiscoveries.Keys.ToArray())
        {
            if (!activeKeys.Contains(key))
            {
                if (device.BayesianDiscoveries.TryRemove(key, out var discovery))
                {
                    device.HassAutoDiscovery.Remove(discovery);
                    await discovery.Delete(mqtt);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private async Task<bool> ClearProbabilityOutputsAsync(Device device)
    {
        var changed = false;

        // Clear the single probabilities topic if there were any probabilities
        if (device.BayesianProbabilities.Count > 0)
        {
            await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/probabilities", null, retain: true);
            device.BayesianProbabilities.Clear();
            changed = true;
        }

        // Clean up auto-discovery entities
        foreach (var entry in device.BayesianDiscoveries.ToArray())
        {
            device.HassAutoDiscovery.Remove(entry.Value);
            await entry.Value.Delete(mqtt);
            device.BayesianDiscoveries.TryRemove(entry.Key, out _);
            changed = true;
        }

        return changed;
    }

    private AutoDiscovery CreateProbabilityDiscovery(Device device, string roomName)
    {
        var sanitizedRoom = SanitizeSegment(roomName);
        var discoveryId = ($"espresense-{device.Id}-{sanitizedRoom}-probability").ToSnakeCase() ?? $"espresense-{device.Id}-{sanitizedRoom}-probability";

        var record = new AutoDiscovery.DiscoveryRecord
        {
            Name = $"{device.Name ?? device.Id} {roomName} Probability",
            UniqueId = $"espresense-companion-{device.Id}-{sanitizedRoom}",
            StateTopic = $"espresense/companion/{device.Id}/probabilities",
            ValueTemplate = $"{{{{ value_json.{sanitizedRoom} | default(0) }}}}",
            EntityStatusTopic = "espresense/companion/status",
            Device = new AutoDiscovery.DeviceRecord
            {
                Name = device.Name ?? device.Id,
                Manufacturer = "ESPresense",
                Model = "Companion",
                SwVersion = "1.0.0",
                Identifiers = new[] { $"espresense-{device.Id}" }
            },
            Origin = new AutoDiscovery.OriginRecord { Name = "ESPresense Companion" },
            StateClass = "measurement",
            Icon = "mdi:account-location"
        };

        return new AutoDiscovery("sensor", discoveryId, record);
    }


    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var snake = value.ToSnakeCase();
        var working = !string.IsNullOrWhiteSpace(snake) ? snake! : value;

        var builder = new StringBuilder();
        foreach (var c in working.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
                builder.Append(c);
            else
                builder.Append('_');
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static bool IsSyntheticRoom(string roomName)
        => roomName.Equals("other", StringComparison.OrdinalIgnoreCase) || roomName.Equals("not_home", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
        {
            await ProcessDevice(device);
        }
    }
}
