using ESPresense.Models;

namespace ESPresense.Events
{
    public class PreviousDeviceDiscoveredEventArgs : EventArgs
    {
        /// <summary>The full discovery topic, e.g. <c>homeassistant/device_tracker/&lt;discovery id&gt;/config</c>.</summary>
        public required string Topic { get; set; }

        /// <summary>The discovery id segment of <see cref="Topic"/>. Always set, including for deletions.</summary>
        public required string DiscoveryId { get; set; }

        /// <summary>
        /// The device id taken from the payload's <c>state_topic</c>. Null when the retained discovery message
        /// was cleared (empty payload), because the discovery topic only carries the discovery id.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>The deserialized payload, or null when the retained discovery message was cleared.</summary>
        public AutoDiscovery? AutoDiscover { get; set; }
    }
}
