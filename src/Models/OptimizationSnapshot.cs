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
        return Measures.ToLookup(a => a.Rx, OptNodeIdComparer.Instance);
    }

    public ILookup<OptNode, Measure> ByTx()
    {
        return Measures.ToLookup(a => a.Tx, OptNodeIdComparer.Instance);
    }

    public string[] GetNodeIds()
    {
        return Measures.SelectMany(m => new[] { m.Rx.Id, m.Tx.Id })
            .Distinct()
            .ToArray();
    }
}

internal sealed class OptNodeIdComparer : IEqualityComparer<OptNode>
{
    public static OptNodeIdComparer Instance { get; } = new();

    public bool Equals(OptNode? x, OptNode? y)
    {
        return ReferenceEquals(x, y) ||
               (x != null && y != null && StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id));
    }

    public int GetHashCode(OptNode obj)
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id);
    }
}

public class OptNode
{
    public string Id { get; set; }
    public string? Name { get; set; }
    public Point3D Location { get; set; }

    /// <summary>
    /// An anchored device rather than a node. A device is ranged with its own rssi@1m (the
    /// <see cref="Measure.RefRssi"/> it was heard with), which no optimizer changes, so its txRef is
    /// that value and not something to fit. That is what lets anchors pin the calibration in absolute
    /// terms: node-to-node data alone cannot tell "every transmitter k dB hot" from "every receiver k dB hot".
    /// </summary>
    public bool IsAnchor { get; set; }
}

public class Measure
{
    public OptNode Rx { get; set; }
    public OptNode Tx { get; set; }

    public double Rssi { get; set; }
    public double? RssiRxAdj { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }

    /// <summary>
    /// The transmit reference this measurement is ranged with when the transmitter is an anchored
    /// device (its rssi@1m), or null for a node, whose txRef is calibrated.
    /// </summary>
    internal double? HeldTxRef => Tx?.IsAnchor == true ? RefRssi : null;

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    internal double GetAdjustedRssi(double? newRxAdjRssi)
    {
        return newRxAdjRssi == null || RssiRxAdj == null ? Rssi : Rssi + RssiRxAdj.Value - newRxAdjRssi.Value;
    }
}
