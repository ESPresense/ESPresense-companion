namespace ESPresense.Models;

public class DeviceToNode(Device device, Node node)
{
    public Device Device { get; } = device;
    public Node Node { get; } = node;

    private double _payloadDistance;

    /// <summary>
    /// Cosine of the angle between the node's antenna boresight and the direction to the device.
    /// Set by the multilateralizer's iterative gain-correction loop; null when no gain correction
    /// is active (falls back to raw payload distance).
    /// </summary>
    public double? NodeCosTheta { get; set; }

    /// <summary>
    /// Computed gain-corrected distance recomputed from RSSI using the cosine gain model G_max·cos²(θ).
    /// Returns null (→ Distance falls back to raw payload) when <see cref="NodeGMaxDb"/> is not set
    /// (not enriched) or when <see cref="NodeAbsorption"/> is zero.
    /// When enriched but <see cref="NodeCosTheta"/> is null, an isotropic pass is used (gainDb = 0).
    /// </summary>
    public double? CorrectedDistance
    {
        get
        {
            if (NodeGMaxDb is null || NodeAbsorption == 0.0)
                return null;

            double gainDb;
            if (NodeCosTheta is null)
            {
                // Isotropic pass: enriched but no directional angle yet
                gainDb = 0.0;
            }
            else
            {
                // Directional: G_max·cos²(θ)
                gainDb = NodeGMaxDb.Value + 10.0 * Math.Log10(Math.Max(NodeCosTheta.Value * NodeCosTheta.Value, 1e-3));
            }

            var dist = Math.Pow(10.0, (-59.0 + NodeRxAdjRssi + gainDb + RefRssi + TxAdjRssi - Rssi) / (10.0 * NodeAbsorption));
            return double.IsNaN(dist) || double.IsInfinity(dist) ? null : dist;
        }
    }

    /// <summary>
    /// Returns the gain-corrected distance if available, otherwise the raw payload distance.
    /// The setter updates the raw payload distance (for initializers and legacy call sites).
    /// </summary>
    public double Distance
    {
        get => CorrectedDistance ?? _payloadDistance;
        set => _payloadDistance = value;
    }

    public double? DistVar { get; set; }

    public double Rssi { get; set; }
    public double? RssiVar { get; set; }
    public double RefRssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    // Enrichment fields populated before the multilateralizer solve loop
    public double NodeAbsorption { get; set; }
    public double NodeRxAdjRssi { get; set; }
    public double? NodeAzimuthRad { get; set; }
    public double? NodeElevationRad { get; set; }
    public double? NodeGMaxDb { get; set; }
    public double TxAdjRssi { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < Device!.Timeout;

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        NewName(payload.Name);
        var moved = Math.Abs(LastDistance - payload.Distance) > 0.25;
        if (moved) LastDistance = payload.Distance;
        _payloadDistance = payload.Distance;
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