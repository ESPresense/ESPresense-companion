using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// The one place that copies persisted <see cref="DeviceSettings"/> onto an operational <see cref="Device"/>
/// before it leaves the process (REST, WebSocket and MCP).
/// </summary>
public class DeviceEnricher(DeviceSettingsStore dss)
{
    /// <summary>Copies ConfiguredRefRssi and, when set, the friendly name from DeviceSettings onto the device.</summary>
    public void Enrich(Device device)
    {
        var deviceSettings = dss.Get(device.Id);
        device.ConfiguredRefRssi = deviceSettings?.RefRssi;
        if (!string.IsNullOrWhiteSpace(deviceSettings?.Name))
            device.Name = deviceSettings.Name;
    }

    /// <summary>Enriches every device in <paramref name="devices"/>.</summary>
    public void Enrich(IEnumerable<Device> devices)
    {
        foreach (var device in devices)
            Enrich(device);
    }
}
