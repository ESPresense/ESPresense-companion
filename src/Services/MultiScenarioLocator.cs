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
        var gps = state?.Config?.Gps;

        if (device.IsAnchored && device.Anchor is { } anchor)
        {
            device.LastCalculated = DateTime.UtcNow;
            device.BestScenario = null;
            var anchored = DeviceReport.From(device, null, gps);

            var stateChanged = device.ReportedState != anchored.State;
            if (stateChanged)
            {
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", anchored.State, retain: true))
                    device.ReportedState = anchored.State;
            }

            var locationChanged = device.ReportedLocation != anchor.Location;

            // Force publication when device transitions to anchored state or location changes
            if (locationChanged || stateChanged)
            {
                if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", anchored.AttributesJson(), retain: true))
                {
                    device.ReportedLocation = anchor.Location;
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
        var report = DeviceReport.From(device, bestScenario, gps);

        if (report.State != device.ReportedState)
        {
            if (await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", report.State, retain: true))
            {
                moved += 1;
                device.ReportedState = report.State;
            }
        }

        if (moved > 0 && bestScenario != null)
        {
            device.ReportedLocation = report.Location!.Value;

            if (!await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", report.AttributesJson(), retain: true))
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
