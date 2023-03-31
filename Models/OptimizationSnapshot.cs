using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class OptimizationSnapshot
{
    public DateTime Timestamp { get; set; }
    public List<OptTxNode> Nodes { get; set; } = new();
    public object Id { get; set; }
}

public class OptTxNode
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, OptRxNode> RxNodes { get; set; } = new();
    public Point3D Location { get; set; }
}

public class OptRxNode
{
    public double Distance { get; set; }
    public Point3D? Location { get; set; }
    public bool Current { get; set; }
    public OptTxNode Tx { get; set; }
    public double RefRssi { get; set; }
    public double Rssi { get; set; }
}