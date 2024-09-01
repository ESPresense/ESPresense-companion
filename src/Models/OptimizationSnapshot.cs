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
}

public class OptNode
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public Point3D Location { get; set; }
}

public class Measure
{
    public bool Current { get; set; }
    public OptNode Rx { get; set; }
    public OptNode Tx { get; set; }
    public double RefRssi { get; set; }
    public double Rssi { get; set; }
    public double Distance { get; set; }
}