using ESPresense.Models;

namespace ESPresense.Events;

public class DeviceSettingsEventArgs
{
    public DeviceSettings Payload { get; set; }
    public string DeviceId { get; set; }
}