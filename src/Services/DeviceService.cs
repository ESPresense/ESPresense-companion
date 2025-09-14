using ESPresense.Controllers;
using ESPresense.Models;
using Microsoft.Extensions.Logging;

namespace ESPresense.Services;

/// <summary>
/// Service that handles common device operations including deletion, cleanup, and management.
/// </summary>
public class DeviceService
{
    private readonly State _state;
    private readonly MqttCoordinator _mqtt;
    private readonly GlobalEventDispatcher _events;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(State state, MqttCoordinator mqtt, GlobalEventDispatcher events, ILogger<DeviceService> logger)
    {
        _state = state;
        _mqtt = mqtt;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Deletes a device and performs all necessary cleanup operations.
    /// </summary>
    /// <param name="deviceId">The ID of the device to delete</param>
    /// <param name="source">The source of the deletion (e.g., "manual", "auto-cleanup")</param>
    /// <returns>True if the device was found and deleted, false if not found</returns>
    public async Task<bool> DeleteAsync(string deviceId, string source = "manual")
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _logger.LogWarning("Attempted to delete device with null or empty ID");
            return false;
        }

        if (!_state.Devices.TryRemove(deviceId, out var device))
        {
            _logger.LogDebug("Device {DeviceId} not found for deletion", deviceId);
            return false;
        }

        await PerformDeviceCleanup(device, source);
        return true;
    }

    /// <summary>
    /// Deletes multiple devices in bulk and performs cleanup operations.
    /// </summary>
    /// <param name="deviceIds">Collection of device IDs to delete</param>
    /// <param name="source">The source of the deletion</param>
    /// <returns>Number of devices successfully deleted</returns>
    public async Task<int> DeleteBulkAsync(IEnumerable<string> deviceIds, string source = "bulk")
    {
        var deletedCount = 0;
        var devices = new List<Device>();

        // First, remove all devices from state
        foreach (var deviceId in deviceIds)
        {
            if (!string.IsNullOrWhiteSpace(deviceId) && _state.Devices.TryRemove(deviceId, out var device))
            {
                devices.Add(device);
                deletedCount++;
            }
        }

        // Then perform cleanup for all removed devices
        foreach (var device in devices)
        {
            await PerformDeviceCleanup(device, source);
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation("Bulk deleted {Count} devices via {Source}", deletedCount, source);
        }

        return deletedCount;
    }

    /// <summary>
    /// Gets devices that haven't been seen since the specified cutoff time.
    /// </summary>
    /// <param name="cutoffTime">Devices last seen before this time are considered inactive</param>
    /// <returns>Collection of inactive devices</returns>
    public IEnumerable<Device> GetInactiveDevices(DateTime cutoffTime)
    {
        return _state.Devices.Values.Where(device =>
        {
            var lastSeen = NormalizeUtc(device.LastSeen);
            return lastSeen.HasValue && lastSeen.Value < cutoffTime;
        });
    }

    /// <summary>
    /// Gets a device by ID.
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    /// <returns>The device if found, null otherwise</returns>
    public Device? GetDevice(string deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? null : _state.Devices.GetValueOrDefault(deviceId);
    }

    /// <summary>
    /// Gets all devices.
    /// </summary>
    /// <returns>Collection of all devices</returns>
    public IEnumerable<Device> GetAllDevices()
    {
        return _state.Devices.Values;
    }

    /// <summary>
    /// Checks if a device exists.
    /// </summary>
    /// <param name="deviceId">The device ID</param>
    /// <returns>True if the device exists, false otherwise</returns>
    public bool DeviceExists(string deviceId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) && _state.Devices.ContainsKey(deviceId);
    }

    private async Task PerformDeviceCleanup(Device device, string source)
    {
        try
        {
            // Clean up Home Assistant auto-discovery entries
            foreach (var ad in device.HassAutoDiscovery)
            {
                await ad.Delete(_mqtt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete HA auto-discovery entries for device {DeviceId}", device.Id);
        }

        // Notify connected clients to remove device immediately
        _events.OnDeviceRemoved(device.Id);

        _logger.LogInformation("Device {DeviceId} ({DeviceName}) deleted via {Source}, last seen: {LastSeen}", 
            device.Id, 
            device.Name ?? "unnamed", 
            source, 
            device.LastSeen?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "never");
    }

    private static DateTime? NormalizeUtc(DateTime? dt)
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
}