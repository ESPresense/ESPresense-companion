namespace ESPresense.Services;

using ESPresense.Models;

/// <summary>
/// Computes BLE distance from RSSI using calibration data (absorption, rxAdj, txAdj)
/// and antenna gain correction. Replaces firmware-reported distances.
/// </summary>
public class DistanceCalculator
{
    private readonly NodeSettingsStore _nodeSettings;
    private readonly State _state;

    public DistanceCalculator(NodeSettingsStore nodeSettings, State state)
    {
        _nodeSettings = nodeSettings;
        _state = state;
    }

    /// <summary>
    /// Compute distance from RSSI for a node-to-node measurement.
    /// Applies antenna gain correction for both Tx and Rx nodes if configured.
    /// </summary>
    public double ComputeNodeDistance(Node txNode, Node rxNode, double rssi, double refRssi)
    {
        var rxNs = _nodeSettings.Get(rxNode.Id);
        var txNs = _nodeSettings.Get(txNode.Id);
        double absorption = rxNs.Calibration?.Absorption ?? 3.0;

        // Compute antenna gain for both nodes
        double gainDb = 0.0;
        if (txNode.HasLocation && rxNode.HasLocation)
        {
            double dx = txNode.Location.X - rxNode.Location.X;
            double dy = txNode.Location.Y - rxNode.Location.Y;
            double dz = txNode.Location.Z - rxNode.Location.Z;

            gainDb += ComputeNodeGain(rxNode, rxNs, dx, dy, dz);
            gainDb += ComputeNodeGain(txNode, txNs, -dx, -dy, -dz);
        }

        return ComputeDistance(rssi, refRssi, absorption, gainDb);
    }

    /// <summary>
    /// Distance from RSSI using log-distance path loss model.
    /// rxAdj/txRef are NOT included — those are RSSI-domain corrections
    /// used by the optimizer, not the distance formula.
    /// </summary>
    private static double ComputeDistance(double rssi, double refRssi, double absorption, double gainDb)
    {
        if (absorption == 0.0) absorption = 3.0;
        double dist = Math.Pow(10.0, (refRssi + gainDb - rssi) / (10.0 * absorption));
        return double.IsNaN(dist) || double.IsInfinity(dist) ? 0.0 : dist;
    }

    private double ComputeNodeGain(Node node, NodeSettings ns, double dx, double dy, double dz)
    {
        var antenna = _state.Config?.ResolveAntenna(node.AntennaProfile);
        if (antenna == null) return 0.0;

        double azDeg = ns.Calibration?.Azimuth ?? 0.0;
        double elDeg = ns.Calibration?.Elevation ?? 0.0;
        double azRad = azDeg * Math.PI / 180.0;
        double elRad = elDeg * Math.PI / 180.0;

        double px = Math.Sin(azRad) * Math.Cos(elRad);
        double py = Math.Cos(azRad) * Math.Cos(elRad);
        double pz = Math.Sin(elRad);

        return Utils.MathUtils.ComputeGainDb(px, py, pz, dx, dy, dz,
            antenna.GMaxDb, antenna.PatternExponent, antenna.BackLoss);
    }
}
