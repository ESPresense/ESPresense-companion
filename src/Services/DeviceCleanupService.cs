using ESPresense.Models;
using Microsoft.Extensions.Logging;

namespace ESPresense.Services;

/// <summary>
/// Background service that periodically removes devices that have not been seen for a configured retention period.
/// </summary>
public class DeviceCleanupService(State state, DeviceService deviceService, ILogger<DeviceCleanupService> logger) : BackgroundService
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

            var inactiveDevices = deviceService.GetInactiveDevices(cutoff).ToList();
            var total = deviceService.GetAllDevices().Count();

            if (inactiveDevices.Count == 0)
            {
                logger.LogInformation("DeviceCleanup: evaluated {Total} devices; 0 removals. Cutoff={Cutoff:o}", total, cutoff);
                return;
            }

            var deviceIds = inactiveDevices.Select(d => d.Id).ToList();
            var removedCount = await deviceService.DeleteBulkAsync(deviceIds, "auto-cleanup");

            logger.LogInformation("DeviceCleanup: evaluated {Total} devices; removed {Removed}. Cutoff={Cutoff:o}", total, removedCount, cutoff);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running device cleanup");
        }
    }
}
