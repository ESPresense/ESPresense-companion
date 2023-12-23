using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace ESPresense.Models;

public class NodeSettings(string id)
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    [StringLength(64)]
    public string? Id { get; set; } = id;

    [JsonPropertyName("absorption")]
    [JsonProperty("absorption")]
    [Range(1, 10)]
    public double? Absorption { get; set; }

    [JsonPropertyName("rx_adj_rssi")]
    [JsonProperty("rx_adj_rssi")]
    [Range(-127, 128)]
    public int? RxAdjRssi { get; set; }

    [JsonPropertyName("tx_ref_rssi")]
    [JsonProperty("tx_ref_rssi")]
    [Range(-127, 128)]
    public int? TxRefRssi { get; set; }

    [JsonPropertyName("max_distance")]
    [JsonProperty("max_distance")]
    [Range(0, 100)]
    public double? MaxDistance { get; set; }

    public NodeSettings Clone()
    {
        return (NodeSettings)MemberwiseClone();
    }
}