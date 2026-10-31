using Newtonsoft.Json;

namespace ESPresense.Models
{
    public class DeviceMessage
    {

        [JsonProperty("distance")]
        public double Distance { get; set; }

        [JsonProperty("var")]
        public double? DistVar { get; set; }

        [JsonProperty("rssi")]
        public double Rssi { get; set; }

        // Firmware before v5.0.0 reports rxAdj (subtracted from the raw reading); v5 reports rawRssi
        // instead. Either way rxAdj = raw - rssi, so it is derived from rawRssi when not reported,
        // and code that un-applies a node's adjustment (rssi + rxAdj - candidate) works for both.
        private double? _rxAdj;

        [JsonProperty("rxAdj")]
        public double? RssiRxAdj
        {
            get => _rxAdj ?? (RawRssi is { } raw ? raw - Rssi : null);
            set => _rxAdj = value;
        }

        /// <summary>The node's smoothed RSSI before its receiver offset (v5 firmware).</summary>
        [JsonProperty("rawRssi")]
        public double? RawRssi { get; set; }

        [JsonProperty("rssiVar")]
        public double? RssiVar { get; set; }

        [JsonProperty("rssi@1m")]
        public double RefRssi { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }
    }
}
