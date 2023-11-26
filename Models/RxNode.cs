using System.Text.Json;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Models;

public class RxNode
{
    public Node? Tx { get; set; }
    public Node? Rx { get; set; }

    public double Distance { get; set; }
    public double Rssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double Expected => Tx?.Location.DistanceTo(Rx!.Location) ?? -1;

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Tx?.Config?.Timeout ?? 30);
    public double RefRssi { get; set; }

    public bool ReadMessage(DeviceMessage payload)
    {
        Distance = payload.Distance;
        RefRssi = payload.RefRssi;
        return NewDistance(payload.Distance);
    }

    private bool NewDistance(double d)
    {
        var moved = Math.Abs(LastDistance - d) > 0.25;
        if (moved) LastDistance = d;
        Distance = d;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}