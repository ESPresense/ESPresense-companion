using ESPresense.Utils;

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
    /// Computed gain-corrected distance recomputed from RSSI using a parametric cos^n(θ) gain model
    /// with configurable back-hemisphere attenuation.
    /// Returns null (→ Distance falls back to raw payload) when <see cref="NodeGMaxDb"/> is not set
    /// (not enriched) or when <see cref="NodeAbsorption"/> is zero.
    /// When enriched but <see cref="NodeCosTheta"/> is null, an isotropic pass is used (gainDb = 0).
    /// </summary>
    public double? CorrectedDistance
    {
        get
        {
            // Capture nullable fields once to avoid race conditions with parallel locators
            var gMaxDb = NodeGMaxDb;
            var cosTheta = NodeCosTheta;

            if (gMaxDb is null || NodeAbsorption == 0.0)
                return null;

            double gainDb;
            if (cosTheta is null)
            {
                // Isotropic pass: enriched but no directional angle yet
                gainDb = 0.0;
            }
            else
            {
                gainDb = MathUtils.ComputeGainDb(cosTheta.Value, gMaxDb.Value, NodePatternExponent, NodeBackLossDb);
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
    public double NodePatternExponent { get; set; }
    public double NodeBackLossDb { get; set; }
    public double TxAdjRssi { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < Device!.Timeout;

    public bool ReadMessage(DeviceMessage payload, double computedDistance)
    {
        Rssi = payload.Rssi;
        RssiVar = payload.RssiVar;
        RefRssi = payload.RefRssi;
        NewName(payload.Name);
        var moved = Math.Abs(LastDistance - computedDistance) > 0.25;
        if (moved) LastDistance = computedDistance;
        _payloadDistance = computedDistance;
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