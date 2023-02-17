using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

public class DeviceSettings
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    [StringLength(32)]
    [RegularExpression("^\\w*$")]
    public string? Id { get; set; }

    [JsonPropertyName("originalId")]
    [JsonProperty("originalId")]
    [StringLength(32)]
    [RegularExpression("^\\w*$")]
    public string? OriginalId { get; set; }

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonPropertyName("rssi@1m")]
    [JsonProperty("rssi@1m")]
    [Range(-127, 128)]
    public int? RefRssi { get; set; }
}