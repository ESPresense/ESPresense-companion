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

            // The anchor fixes where the device is, not whether it is here: once no node hears it, it's away.
            var present = device.Nodes.Values.Any(dn => dn.Current);
            var anchorState = present ? device.Room?.Name ?? device.Floor?.Name ?? "not_home" : "not_home";
            var stateChanged = device.ReportedState != anchorState;
            if (stateChanged)
            {
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", anchorState, retain: true))
                    device.ReportedState = anchorState;
            }

            var locationChanged = device.ReportedLocation != anchorLocation;
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
                {
                    device.ReportedLocation = anchorLocation;
                    globalEventDispatcher.OnDeviceChanged(device, false);
                }
                else
                {
                    // Forget the state too, so the next pass retries even when the location already matches
                    device.ReportedState = null;
                }
            }

            return;
        }

        var fusion = ScenarioFusion.Update(device, state?.Config?.Filtering ?? new ConfigFiltering(), DateTime.UtcNow);
        var bestScenario = fusion.Best;
        var moved = fusion.Moved;

        // -----------------------------------------------------------------
        // 6. Publish state / attributes if anything moved ----------------------
        // -----------------------------------------------------------------
        if (bestScenario != null)
        {
            var newState = device.Room?.Name ?? device.Floor?.Name ?? "not_home";
            if (newState != device.ReportedState)
            {
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", newState, retain: true))
                {
                    moved += 1;
                    device.ReportedState = newState;
                }
            }
        }
        else if (device.ReportedState != "not_home")
        {
            if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", "not_home", retain: true))
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
                confidence = bestScenario.Confidence,
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
                if (state.Config?.Locators?.MultiFloor?.Enabled ?? false) active.Add("MultiFloor");
                if (state.Config?.Locators?.NearestNode?.Enabled ?? false) active.Add("NearestNode");

                UpdateStatus(active.Count > 0 ? string.Join(", ", active) : "None");

                await using (lease)
                {
                    Log.Information("Acquired lease '{LeaseName}'", LocatingLeaseName);

                    await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
                    {
                        if (!lease.HasLease()) break;

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
