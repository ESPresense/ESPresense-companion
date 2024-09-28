using ESPresense.Models;

namespace ESPresense.Events
{
    public class PreviousDeviceDiscoveredEventArgs : EventArgs
    {
        public required AutoDiscovery AutoDiscover { get; set; }
    }
}
