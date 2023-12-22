namespace ESPresense.Models;

public class NodeToNode(Node tx, Node rx)
{
    public Node Tx { get; } = tx;
    public Node Rx { get; } = rx;

    public double Distance { get; set; }
    public double Rssi { get; set; }
    public double RefRssi { get; set; }
    public double? Variance { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }
    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(30);

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RefRssi = payload.RefRssi;
        Variance = payload.Variance;
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        Distance = payload.Distance;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}