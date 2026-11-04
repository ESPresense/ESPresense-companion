using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// The one place that copies persisted <see cref="DeviceSettings"/> onto an operational <see cref="Device"/>
/// before it leaves the process (REST, WebSocket and MCP).
/// </summary>
public class DeviceEnricher(DeviceSettingsStore dss)
{
    /// <summary>Copies ConfiguredRefRssi and, when set, the friendly name from DeviceSettings onto the device.</summary>
    public void Enrich(Device device) => ApplySettings(device, dss.Get(device.Id));

    /// <summary>
    /// The settings fields every path copies onto a device: ConfiguredRefRssi always (null clears it), the name
    /// only when set. Shared with <see cref="DeviceSettingsStore.ApplyToDevice"/>, which adds the anchor.
    /// </summary>
    public static void ApplySettings(Device device, DeviceSettings? settings)
    {
        device.ConfiguredRefRssi = settings?.RefRssi;
        if (!string.IsNullOrWhiteSpace(settings?.Name))
            device.Name = settings.Name;
    }

    /// <summary>Enriches every device in <paramref name="devices"/>.</summary>
    public void Enrich(IEnumerable<Device> devices)
    {
        foreach (var device in devices)
            Enrich(device);
    }
}
