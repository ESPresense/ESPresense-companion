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
    /// An anchored device rather than a node. A device is ranged with the rssi@1m it was heard with (the
    /// <see cref="Measure.RefRssi"/>), which no optimizer changes, so its txRef is that value and not
    /// something to fit. That is what lets anchors pin the calibration in the frame devices are ranged in:
    /// node-to-node data alone cannot tell "every transmitter k dB hot" from "every receiver k dB hot".
    /// How absolute that frame is depends on where the rssi@1m came from; see <see cref="Measure.HeldTxRef"/>.
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
    /// The transmit reference this measurement is ranged with when the transmitter is an anchored device
    /// (its rssi@1m), or null for a node, whose txRef is calibrated, and for an anchor reading with no usable
    /// rssi@1m (<see cref="IsUnusableAnchor"/>), which the optimizer and scoring leave out.
    ///
    /// The value is only a property of the device when the firmware took it from the device's own
    /// calibration: a configured rssi@1m, or an iBeacon/Eddystone measured power. Otherwise the firmware
    /// derives it as the receiving node's ref_rssi plus the TX power the device advertises, or a fixed guess
    /// at it, so it carries that node's ref_rssi and any error in the guess. Holding it is still exactly
    /// how the device is ranged, so rxAdj is placed right for that device as the node reports it; but an
    /// error in that guess then lands in the receiver's rxAdj rather than being something the fit can overrule.
    /// </summary>
    internal double? HeldTxRef => Tx?.IsAnchor == true && HasUsableRefRssi ? RefRssi : null;

    /// <summary>
    /// An anchored device's reading without a usable rssi@1m (0, as <see cref="Device.MeasuredRefRssi"/> treats it,
    /// or not finite). There is nothing to hold its txRef at, and it must not be fitted like a node either (that
    /// would propose a txRef for a device id), so it is left out. <see cref="State.ReadMeasures"/> already drops
    /// these; the check is here too so a measure built anywhere else cannot pin the fit to 0 dBm.
    /// </summary>
    internal bool IsUnusableAnchor => Tx?.IsAnchor == true && !HasUsableRefRssi;

    private bool HasUsableRefRssi => RefRssi != 0 && double.IsFinite(RefRssi);

    public double Distance { get; set; }
    public double? DistVar { get; set; }

    internal double GetAdjustedRssi(double? newRxAdjRssi)
    {
        return newRxAdjRssi == null || RssiRxAdj == null ? Rssi : Rssi + RssiRxAdj.Value - newRxAdjRssi.Value;
    }
}
