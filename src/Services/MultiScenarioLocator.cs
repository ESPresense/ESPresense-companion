using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using ESPresense.Extensions;
using System.Text.Json;
using ESPresense.Events;
using Serilog;

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
                                   DeviceHistoryStore deviceHistory,
                                   ILeaseService leaseService) : BackgroundService
{
    private const string LocatingLeaseName = "locating";

    internal async Task ProcessDevice(Device device)
    {
        if (device.IsAnchored && device.Anchor is { } anchor)
        {
            device.LastCalculated = DateTime.UtcNow;
            var anchorLocation = anchor.Location;

            var gps = state?.Config?.Gps;
            var (lat, lon) = gps?.Report == true ? gps.Add(anchorLocation.X, anchorLocation.Y) : (null, null);
            var elevation = gps?.Report == true ? anchorLocation.Z + gps?.Elevation : null;

            var stateChanged = device.ReportedState != "not_home";
            if (stateChanged)
            {
                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", "not_home");
                device.ReportedState = "not_home";
            }

            var locationChanged = device.ReportedLocation != anchorLocation;
            device.ReportedLocation = anchorLocation;
            device.BestScenario = null;

            // Force publication when device transitions to anchored state or location changes
            if (locationChanged || stateChanged)
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    source_type = "espresense",
                    latitude = lat,
                    longitude = lon,
                    elevation,
                    x = anchorLocation.X,
                    y = anchorLocation.Y,
                    z = anchorLocation.Z,
                    confidence = 100,
                    fixes = 0,
                    best_scenario = "Anchored",
                    last_seen = device.LastSeen
                }, SerializerSettings.NullIgnore);

                await mqtt.EnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true);
                globalEventDispatcher.OnDeviceChanged(device, false);
            }

            return;
        }

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
        var filtering = state?.Config?.Filtering ?? new ConfigFiltering();
        foreach (var scenario in device.Scenarios)
        {
            if (!scenario.Current)
            {
                scenario.WeightedConfidence = 0;
                continue;
            }

            double delta = predictedLocation.DistanceTo(scenario.Location);
            double mcw = Math.Exp(-(delta * delta) / (2 * filtering.MotionSigma * filtering.MotionSigma));
            scenario.WeightedConfidence = (scenario.Confidence ?? 0) * mcw;
        }

        // -----------------------------------------------------------------
        // 4. Inline probability smoothing (was UpdateScenarioProbabilities)
        // -----------------------------------------------------------------
        double totalWeightedConfidence = device.Scenarios.Sum(s => s.WeightedConfidence);

        if (totalWeightedConfidence > 0)
        {
            var priorWeight = filtering.SmoothingWeight;
            var newDataWeight = 1.0 - priorWeight;

            foreach (var scenario in device.Scenarios)
            {
                double newProb = scenario.WeightedConfidence / totalWeightedConfidence;
                scenario.Probability = priorWeight * scenario.Probability + newDataWeight * newProb;
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
        else if (device.ReportedState != "not_home")
        {
            moved += 1;
            await mqtt.EnqueueAsync($"espresense/companion/{device.Id}", "not_home");
            device.ReportedState = "not_home";
        }

        if (moved > 0 && bestScenario != null)
        {
            device.ReportedLocation = device.Location ?? new Point3D();

            var gps = state?.Config?.Gps;
            var location = device.Location ?? new Point3D();

            // Only include GPS coordinates if reporting is enabled
            var (lat, lon) = gps?.Report == true ? gps.Add(location.X, location.Y) : (null, null);
            var elevation = gps?.Report == true ? location.Z + gps?.Elevation : null;

            var payload = JsonConvert.SerializeObject(new
            {
                source_type = "espresense",
                latitude = lat,
                longitude = lon,
                elevation = elevation,
                x = location.X,
                y = location.Y,
                z = location.Z,
                confidence = bestScenario.Confidence,
                fixes = bestScenario.Fixes,
                best_scenario = bestScenario.Name,
                last_seen = device.LastSeen
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
                        Id = device.Id,
                        When = DateTime.UtcNow,
                        X = ds.Location.X,
                        Y = ds.Location.Y,
                        Z = ds.Location.Z,
                        Confidence = ds.Confidence ?? 0,
                        Fixes = ds.Fixes ?? 0,
                        Scenario = ds.Name,
                        Best = ds == bestScenario
                    });
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateStatus("Started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LeaseHandle? lease = null;
                while (lease == null && !stoppingToken.IsCancellationRequested)
                {
                    UpdateStatus("Acquiring lease...");
                    lease = await leaseService.AcquireAsync(
                        LocatingLeaseName,
                        timeout: TimeSpan.FromSeconds(60),
                        cancellationToken: stoppingToken);

                    if (lease == null)
                    {
                        Log.Warning("Still waiting for lease '{LeaseName}'...", LocatingLeaseName);
                    }
                }

                if (lease == null) return;

                var active = new List<string>();
                if (state.Config?.Locators?.NadarayaWatson?.Enabled ?? false) active.Add("NadarayaWatson");
                if (state.Config?.Locators?.NelderMead?.Enabled ?? false) active.Add("NelderMead");
                if (state.Config?.Locators?.Bfgs?.Enabled ?? false) active.Add("Bfgs");
                if (state.Config?.Locators?.Mle?.Enabled ?? false) active.Add("Mle");
                if (state.Config?.Locators?.NearestNode?.Enabled ?? false) active.Add("NearestNode");

                UpdateStatus(active.Count > 0 ? string.Join(", ", active) : "None");

                await using (lease)
                {
                    Log.Information("Acquired lease '{LeaseName}'", LocatingLeaseName);
                    await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
                    {
                        if (!lease.HasLease())
                            break;

                        await ProcessDevice(device);
                    }
                }

                UpdateStatus("Lost lease");
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                UpdateStatus("Error: " + ex.Message);
                Log.Error(ex, "Error in MultiScenarioLocator");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private void UpdateStatus(string status)
    {
        if (state.LocatorState.Status == status) return;
        state.LocatorState.Status = status;
        globalEventDispatcher.OnLocatorStateChanged(state.LocatorState);
    }
}
