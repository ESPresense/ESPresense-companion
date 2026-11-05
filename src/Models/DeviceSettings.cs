using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

public class DeviceSettings
{
    [JsonPropertyName("id")]
    [StringLength(64)]
    public string? Id { get; set; }

    [JsonPropertyName("originalId")]
    [StringLength(64)]
    public string? OriginalId { get; set; }

    [JsonPropertyName("name")]
    [StringLength(128)]
    public string? Name { get; set; }

    [JsonPropertyName("rssi@1m")]
    [Range(-127, 128)]
    public int? RefRssi { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("z")]
    public double? Z { get; set; }

    [JsonIgnore]
    public bool HasAnchor => X.HasValue && Y.HasValue && Z.HasValue;
}
