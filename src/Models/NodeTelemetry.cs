using Newtonsoft.Json;

namespace ESPresense.Models;

public class NodeTelemetry
{
    [JsonProperty("ip")]
    public string? Ip { get; set; }

    [JsonProperty("uptime")]
    public int Uptime { get; set; }

    [JsonProperty("firm")]
    public string? Firmware { get; set; }


    [JsonProperty("rssi")]
    public int Rssi { get; set; }

    [JsonProperty("ver")]
    public string? Version { get; set; }

    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonProperty("adverts")]
    public int Adverts { get; set; }

    [JsonProperty("seen")]
    public int Seen { get; set; }

    [JsonProperty("reported")]
    public int Reported { get; set; }

    [JsonProperty("freeHeap")]
    public int FreeHeap { get; set; }

    [JsonProperty("maxAllocHeap")]
    public int MaxAllocHeap { get; set; }

    [JsonProperty("memFrag")]
    public double MemoryFragmentation { get; set; }

    [JsonProperty("scanHighWater")]
    public int ScanHighWater { get; set; }

}