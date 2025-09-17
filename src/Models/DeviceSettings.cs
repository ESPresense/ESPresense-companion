using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

public class DeviceSettings
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    [StringLength(64)]
    public string? Id { get; set; }

    [JsonPropertyName("originalId")]
    [JsonProperty("originalId")]
    [StringLength(64)]
    public string? OriginalId { get; set; }

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    [StringLength(128)]
    public string? Name { get; set; }

    [JsonPropertyName("rssi@1m")]
    [JsonProperty("rssi@1m")]
    [Range(-127, 128)]
    public int? RefRssi { get; set; }

    [JsonPropertyName("x")]
    [JsonProperty("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    [JsonProperty("y")]
    public double? Y { get; set; }

    [JsonPropertyName("z")]
    [JsonProperty("z")]
    public double? Z { get; set; }
}