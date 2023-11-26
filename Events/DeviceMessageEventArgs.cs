using ESPresense.Models;

namespace ESPresense.Events;

public class DeviceMessageEventArgs
{
    public string DeviceId { get; set; }
    public string NodeId { get; set; }
    public DeviceMessage Payload { get; set; }
}