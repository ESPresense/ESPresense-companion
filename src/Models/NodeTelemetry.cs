using ESPresense.Utils;

namespace ESPresense.Models;

/// <summary>
/// A node's <c>telemetry</c> payload. Also returned by the API (<see cref="NodeStateTele.Telemetry"/>) under the
/// declared names; <see cref="MqttNameAttribute"/> gives the firmware's name where it differs.
/// </summary>
public class NodeTelemetry
{
    public string? Ip { get; set; }

    public int Uptime { get; set; }

    [MqttName("firm")]
    public string? Firmware { get; set; }

    public string? Board { get; set; }

    public int Rssi { get; set; }

    [MqttName("ver")]
    public string? Version { get; set; }

    public int Count { get; set; }

    public int Adverts { get; set; }

    public int Seen { get; set; }

    public int Reported { get; set; }

    public int FreeHeap { get; set; }

    public int MaxAllocHeap { get; set; }

    [MqttName("memFrag")]
    public double MemoryFragmentation { get; set; }

    public int ScanHighWater { get; set; }

}