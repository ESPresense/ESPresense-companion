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

        [JsonProperty("rssiVar")]
        public double? RssiVar { get; set; }

        [JsonProperty("rssi@1m")]
        public double RefRssi { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }
    }
}
