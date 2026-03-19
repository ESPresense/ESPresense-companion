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
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home"))
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

                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true))
                    globalEventDispatcher.OnDeviceChanged(device, false);
            }

            return;
        }

        // -----------------------------------------------------------------
        // 1. Refresh all scenarios -------------------------------------------------
        // -----------------------------------------------------------------
        device.LastCalculated = DateTime.UtcNow;
        var filtering = state?.Config?.Filtering ?? new ConfigFiltering();
        var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

        // -----------------------------------------------------------------
        // 1b. Softmax confidence across all current scenarios ----------------
        // -----------------------------------------------------------------
        var currentScenarios = device.Scenarios.Where(s => s.Current).ToArray();
        if (currentScenarios.Length > 0)
        {
            var temperature = filtering.ConfidenceTemperature;
            var logits = currentScenarios.Select(s => MathUtils.ScenarioLogit(s.Rmse, s.PearsonCorrelation, s.Fixes, temperature)).ToArray();
            var probs = MathUtils.Softmax(logits);
            for (int i = 0; i < currentScenarios.Length; i++)
                currentScenarios[i].Confidence = probs[i];
        }

        // Zero out non-current scenarios
        foreach (var s in device.Scenarios.Where(s => !s.Current))
            s.Confidence = 0.0;

        // -----------------------------------------------------------------
        // 2. Get Kalman prediction from current state (for motion consistency)
        // -----------------------------------------------------------------
        // Prediction comes BEFORE update; the blended position feeds Kalman in step 5
        var (predictedLocation, _) = device.KalmanFilter.GetPrediction();

        // -----------------------------------------------------------------
        // 3. Motion‑consistency weighting for every scenario -------------------
        //    Only applied within the current floor; cross-floor scenarios use
        //    raw confidence so that genuine floor transitions aren't blocked.
        // -----------------------------------------------------------------
        var currentFloor = device.EnsembleFloor;
        foreach (var scenario in device.Scenarios)
        {
            if (!scenario.Current)
            {
                scenario.WeightedConfidence = 0;
                continue;
            }

            if (currentFloor != null && scenario.Floor != currentFloor)
            {
                // Cross-floor: use raw confidence (no motion penalty)
                scenario.WeightedConfidence = scenario.Confidence ?? 0;
            }
            else
            {
                // Same floor (or no prior floor): apply motion consistency
                double delta = predictedLocation.DistanceTo(scenario.Location);
                double mcw = Math.Exp(-(delta * delta) / (2 * filtering.MotionSigma * filtering.MotionSigma));
                scenario.WeightedConfidence = (scenario.Confidence ?? 0) * mcw;
            }
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
        // 5. Floor-level ensemble (Monte Carlo aggregation)
        // -----------------------------------------------------------------
        var previousScenario = device.BestScenario;
        var previousFloor = device.EnsembleFloor;
        var hysteresis = filtering.ScenarioHysteresis;

        // 5a. Group scenarios by floor and sum probabilities
        var floorGroups = currentScenarios
            .GroupBy(s => s.Floor)
            .Select(g => new
            {
                Floor = g.Key,
                TotalProb = g.Sum(s => s.Probability),
                Scenarios = g.ToArray()
            })
            .ToArray();

        Scenario? bestScenario = null;

        if (floorGroups.Length > 0)
        {
            // 5b. Pick winning floor with hysteresis
            var bestFloorGroup = floorGroups
                .OrderByDescending(f => f.TotalProb + (f.Floor == previousFloor ? hysteresis : 0))
                .First();

            // Log floor changes only
            if (previousFloor != null && bestFloorGroup.Floor != previousFloor)
                Log.Information("Device {DeviceId} switched floor {From} -> {To} (confidence {Confidence:P0})",
                    device.Id, previousFloor.Name, bestFloorGroup.Floor?.Name, bestFloorGroup.TotalProb);

            // 5c. Weighted average position within winning floor
            var floorScenarios = bestFloorGroup.Scenarios;
            var totalProb = bestFloorGroup.TotalProb;

            Point3D blendedPosition;
            if (totalProb > 0)
            {
                var blendedX = floorScenarios.Sum(s => s.Location.X * s.Probability) / totalProb;
                var blendedY = floorScenarios.Sum(s => s.Location.Y * s.Probability) / totalProb;
                var blendedZ = floorScenarios.Sum(s => s.Location.Z * s.Probability) / totalProb;
                blendedPosition = new Point3D(blendedX, blendedY, blendedZ);
            }
            else
            {
                // Fallback: use highest-confidence scenario location
                blendedPosition = floorScenarios
                    .OrderByDescending(s => s.Confidence)
                    .First().Location;
            }

            // Feed Kalman with blended position
            device.UpdateLocation(blendedPosition);

            // 5d. Best scenario = highest-prob on winning floor (for best_scenario attribute)
            bestScenario = floorScenarios
                .OrderByDescending(s => s.Probability)
                .ThenByDescending(s => s.Confidence)
                .First();
            device.BestScenario = bestScenario;

            // 5e. Set ensemble properties
            device.EnsembleFloor = bestFloorGroup.Floor;
            device.FloorConfidence = bestFloorGroup.TotalProb;

            // Determine room from Kalman-filtered blended position
            var kalmanLocation = device.Location ?? blendedPosition;
            device.EnsembleRoom = SpatialUtils.FindRoomContaining(kalmanLocation, bestFloorGroup.Floor);
        }
        else
        {
            device.BestScenario = null;
            device.EnsembleFloor = null;
            device.EnsembleRoom = null;
            device.FloorConfidence = null;
        }

        if (device.Debug)
        {
            foreach (var s in device.Scenarios)
            {
                var best = s == bestScenario ? "*" : " ";
                var prev = s == previousScenario ? ">" : " ";
                Log.Information("{DeviceId} {Best}{Prev} {Scenario,-20} rmse={Rmse,6:F2} r={Pearson,6:F2} fixes={Fixes} conf={Confidence:P0} mcw={WeightedConf:F3} prob={Probability:F3} loc=({X:F1},{Y:F1},{Z:F1})",
                    device.Id, best, prev, s.Name ?? "?",
                    s.Rmse ?? -1, s.PearsonCorrelation ?? 0, s.Fixes ?? 0,
                    s.Confidence ?? 0, s.WeightedConfidence, s.Probability,
                    s.Location.X, s.Location.Y, s.Location.Z);
            }

            // Show floor aggregation
            foreach (var fg in floorGroups.OrderByDescending(f => f.TotalProb))
            {
                var winner = fg.Floor == device.EnsembleFloor ? "*" : " ";
                Log.Information("{DeviceId} {Winner} Floor={Floor,-15} prob={TotalProb:F3} scenarios={Count}",
                    device.Id, winner, fg.Floor?.Name ?? "?", fg.TotalProb, fg.Scenarios.Length);
            }

            if (bestScenario != null)
            {
                var blendLoc = device.Location ?? new Point3D();
                Log.Information("{DeviceId}   Blended: floor={Floor} room={Room} conf={Confidence:F3} loc=({X:F1},{Y:F1},{Z:F1})",
                    device.Id, device.EnsembleFloor?.Name, device.EnsembleRoom?.Name,
                    device.FloorConfidence, blendLoc.X, blendLoc.Y, blendLoc.Z);
            }
        }

        // -----------------------------------------------------------------
        // 6. Publish state / attributes if anything moved ----------------------
        // -----------------------------------------------------------------
        if (bestScenario != null)
        {
            var newState = device.Room?.Name ?? device.Floor?.Name ?? "not_home";
            if (newState != device.ReportedState)
            {
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", newState))
                {
                    moved += 1;
                    device.ReportedState = newState;
                }
            }
        }
        else if (device.ReportedState != "not_home")
        {
            if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home"))
            {
                moved += 1;
                device.ReportedState = "not_home";
            }
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
                confidence = device.FloorConfidence,
                fixes = bestScenario.Fixes,
                best_scenario = bestScenario.Name,
                last_seen = device.LastSeen
            }, SerializerSettings.NullIgnore);

            if (!await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", payload, retain: true))
                return;

            globalEventDispatcher.OnDeviceChanged(device, false);

            // optional history
            if (state?.Config?.History?.Enabled ?? false)
            {
                foreach (var ds in device.Scenarios.Where(ds => ds.Confidence > 0))
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
