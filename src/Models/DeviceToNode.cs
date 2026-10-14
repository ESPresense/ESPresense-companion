namespace ESPresense.Models;

public class DeviceToNode(Device device, Node node)
{
    public Device Device { get; } = device;
    public Node Node { get; } = node;

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }
    /// <summary>The node's rxAdj when it sent this reading; the firmware publishes rssi = raw - rxAdj.</summary>
    public double? RssiRxAdj { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < Device!.Timeout;

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        RssiRxAdj = payload.RssiRxAdj;
        NewName(payload.Name);
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        Distance = payload.Distance;
        DistVar = payload.DistVar;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }

    private void NewName(string? name)
    {
        if (Device == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (Device.Name == name) return;
        Device.Name = name;
        Device.Check = true;
    }
}