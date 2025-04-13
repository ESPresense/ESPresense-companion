namespace ESPresense.Models;

public class RxNode
{
    public Node? Tx { get; set; }
    public Node? Rx { get; set; }

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    public double? RssiRxAdj { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double MapDistance => Tx?.Location.DistanceTo(Rx!.Location) ?? -1;

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Tx?.Config?.Timeout ?? 30);

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RssiRxAdj = payload.RssiRxAdj;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        Distance = payload.Distance;
        DistVar = payload.DistVar;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}