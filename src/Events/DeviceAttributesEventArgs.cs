using System;
using System.Collections.Generic;

namespace ESPresense.Events
{
    public class DeviceAttributesEventArgs : EventArgs
    {
        public string DeviceId { get; set; } = string.Empty;
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
    }
}