using ESPresense.Models;
using ESPresense.Controllers;
using ESPresense.Events;

namespace ESPresense.Services;

/// <summary>
/// Background service that, each time a <see cref="Device"/> surfaces from the tracker pipeline:
///   1. Fuses its scenarios (<see cref="ScenarioFusion.Update"/>), unless it is anchored.
///   2. Builds one <see cref="DeviceReport"/> for it.
///   3. Publishes the report (<see cref="IDeviceStatePublisher"/>).
///   4. When the attributes went out: raises OnDeviceChanged and, for located devices, records history.
/// </summary>
public class MultiScenarioLocator(DeviceTracker dl,
                                   State state,
                                   IDeviceStatePublisher publisher,
                                   IDeviceHistoryWriter history,
                                   GlobalEventDispatcher globalEventDispatcher,
                                   ILeaseService leaseService,
                                   ILogger<MultiScenarioLocator> logger) : BackgroundService
{
    private const string LocatingLeaseName = "locating";

    internal async Task ProcessDevice(Device device, CancellationToken ct = default)
    {
        var config = state.Config;
        var now = DateTime.UtcNow;

        Scenario? best = null;
        bool locationChanged;
        if (device.Anchor is { } anchor)
        {
            device.LastCalculated = now;
            device.BestScenario = null;
            // Becoming anchored (best_scenario turns "Anchored") or moving the anchor changes what HA shows.
            var reported = publisher.Reported(device);
            locationChanged = reported.Location != anchor.Location || reported.BestScenario != DeviceReport.AnchoredScenario;
        }
        else
        {
            var fusion = ScenarioFusion.Update(device, config?.Filtering ?? new ConfigFiltering(), now);
            best = fusion.Best;
            // A locator reporting a new position; the Kalman output drifts every pass, so it isn't compared.
            locationChanged = fusion.Moved > 0;
        }

        var report = DeviceReport.From(device, best, config?.Gps);
        var outcome = await publisher.PublishAsync(device, report, locationChanged, ct);
        if (!outcome.AttributesPublished) return;

        globalEventDispatcher.OnDeviceChanged(device, false);
        if (!device.IsAnchored)
            await history.RecordAsync(device, best, ct);
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
                        logger.LogWarning("Still waiting for lease '{LeaseName}'...", LocatingLeaseName);
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
                    logger.LogInformation("Acquired lease '{LeaseName}'", LocatingLeaseName);

                    await foreach (var device in dl.GetConsumingEnumerable(stoppingToken))
                    {
                        if (!lease.HasLease()) break;

                        await ProcessDevice(device, stoppingToken);
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
                logger.LogError(ex, "Error in MultiScenarioLocator");
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
