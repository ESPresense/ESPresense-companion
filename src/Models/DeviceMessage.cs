using System.Text.Json.Serialization;

namespace ESPresense.Models
{
    public class DeviceMessage
    {

        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("var")]
        public double? DistVar { get; set; }

        [JsonPropertyName("rssi")]
        public double Rssi { get; set; }

        // Firmware before v5.0.0 reports rxAdj (subtracted from the raw reading); v5 reports rawRssi
        // instead. Either way rxAdj = raw - rssi, so it is derived from rawRssi when not reported,
        // and code that un-applies a node's adjustment (rssi + rxAdj - candidate) works for both.
        private double? _rxAdj;

        [JsonPropertyName("rxAdj")]
        public double? RssiRxAdj
        {
            get => _rxAdj ?? (RawRssi is { } raw ? raw - Rssi : null);
            set => _rxAdj = value;
        }

        /// <summary>The node's smoothed RSSI before its receiver offset (v5 firmware).</summary>
        [JsonPropertyName("rawRssi")]
        public double? RawRssi { get; set; }

        [JsonPropertyName("rssiVar")]
        public double? RssiVar { get; set; }

        [JsonPropertyName("rssi@1m")]
        public double RefRssi { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
