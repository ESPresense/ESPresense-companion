using ESPresense.Controllers;
using ESPresense.Models;
using Serilog;
using Microsoft.Extensions.Logging;

namespace ESPresense.Services;

/// <summary>
/// Background service that periodically removes devices that have not been seen for a configured retention period.
/// </summary>
public class DeviceCleanupService(State state, MqttCoordinator mqtt, GlobalEventDispatcher events, ILogger<DeviceCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = state.Config?.DeviceRetentionTimeSpan ?? TimeSpan.FromDays(30);
        logger.LogInformation("DeviceCleanup: starting. Retention={Retention}", retention);

        // Give MQTT attribute restoration a moment to populate LastSeen
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (TaskCanceledException) { }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCleanup(stoppingToken);

            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (TaskCanceledException) { /* ignored */ }
        }
    }

    private async Task RunCleanup(CancellationToken stoppingToken)
    {
        try
        {
            var retention = state.Config?.DeviceRetentionTimeSpan ?? TimeSpan.FromDays(30);
            var cutoff = DateTime.UtcNow - retention;

            DateTime? NormalizeUtc(DateTime? dt)
            {
                if (dt == null) return null;
                var v = dt.Value;
                return v.Kind switch
                {
                    DateTimeKind.Utc => v,
                    DateTimeKind.Local => v.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(v, DateTimeKind.Utc)
                };
            }

            var toRemove = new List<Device>();
            foreach (var d in state.Devices.Values)
            {
                // Use LastSeen exclusively; this is restored at startup from retained attributes
                var lastSeen = NormalizeUtc(d.LastSeen);
                if (lastSeen.HasValue && lastSeen.Value < cutoff)
                {
                    toRemove.Add(d);
                }
            }

            var total = state.Devices.Count;
            if (toRemove.Count == 0)
            {
                logger.LogInformation("DeviceCleanup: evaluated {Total} devices; 0 removals. Cutoff={Cutoff:o}", total, cutoff);
                return;
            }

            var removedCount = 0;
            foreach (var d in toRemove)
            {
                if (state.Devices.TryRemove(d.Id, out var removed))
                {
                    removedCount++;
                    try
                    {
                        foreach (var ad in removed.HassAutoDiscovery)
                        {
                            await ad.Delete(mqtt);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "DeviceCleanup: failed HA discovery delete for {DeviceId}", removed.Id);
                    }

                    // Notify connected clients to drop device immediately
                    events.OnDeviceRemoved(removed.Id);

                    Log.Information("[x] Auto-deleted device {DeviceId} last seen {LastSeen}", removed.Id, removed.LastSeen?.ToLocalTime());
                }
            }

            logger.LogInformation("DeviceCleanup: evaluated {Total} devices; removed {Removed}. Cutoff={Cutoff:o}", total, removedCount, cutoff);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running device cleanup");
        }
    }
}
