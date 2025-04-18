using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class OptimizationSnapshot
{
    public DateTime Timestamp { get; set; }
    public List<Measure> Measures { get; set; } = new();

    public OptimizationSnapshot()
    {
        Timestamp = DateTime.UtcNow;
    }

    public ILookup<OptNode, Measure> ByRx()
    {
        return Measures.ToLookup(a => a.Rx);
    }

    public ILookup<OptNode, Measure> ByTx()
    {
        return Measures.ToLookup(a => a.Tx);
    }

    public string[] GetNodeIds()
    {
        return Measures.SelectMany(m => new[] { m.Rx.Id, m.Tx.Id })
            .Distinct()
            .ToArray();
    }
}

public class OptNode
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public Point3D Location { get; set; }
}

public class Measure
{
    public OptNode Rx { get; set; }
    public OptNode Tx { get; set; }

    public double Rssi { get; set; }
    public double? RssiRxAdj { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    internal double GetAdjustedRssi(double? newRxAdjRssi)
    {
        return newRxAdjRssi == null || RssiRxAdj == null ? Rssi : Rssi + RssiRxAdj.Value - newRxAdjRssi.Value;
    }
}