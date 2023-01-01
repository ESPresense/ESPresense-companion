using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

public class DeviceSettings
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonPropertyName("originalId")]
    [JsonProperty("originalId")]
    public string? OriginalId { get; set; }

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonPropertyName("rssi@1m")]
    [JsonProperty("rssi@1m")]
    public int? RefRssi { get; set; }
}