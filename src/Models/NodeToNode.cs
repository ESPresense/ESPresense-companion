namespace ESPresense.Models;

public class NodeToNode(Node tx, Node rx)
{
    public Node Tx { get; } = tx;
    public Node Rx { get; } = rx;

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    public double? RssiVar { get; private set; }
    public double RefRssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }
    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(30);

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        DistVar = payload.DistVar;
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        Distance = payload.Distance;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}