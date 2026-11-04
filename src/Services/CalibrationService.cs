using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Services;

/// <summary>
/// Builds the calibration matrix. The one implementation behind GET /api/state/calibration and the MCP
/// <c>get_calibration</c> tool and <c>state://calibration</c> resource.
/// </summary>
public class CalibrationService(State state, NodeSettingsStore nsd)
{
    private const int MinPairsForBestMetrics = 5;

    /// <summary>
    /// Builds the full matrix: every current node-to-node pair, then every anchored device as a transmitter
    /// to the located nodes that currently hear it. R and RMSE compare map distance with measured distance
    /// over every pair where both are positive; the best-seen values are recorded once there are enough pairs.
    /// </summary>
    public Calibration Calculate()
    {
        var c = new Calibration();
        c.OptimizerState = state.OptimizerState;

        var mapDistances = new List<double>();
        var actualDistances = new List<double>();

        // One settings lookup per node per call, not one per pair.
        var settings = new Dictionary<string, NodeSettings>();
        NodeSettings Settings(string id) => settings.TryGetValue(id, out var ns) ? ns : settings[id] = nsd.Get(id);

        // Process traditional Node-to-Node relationships
        foreach (var (txId, tx) in state.Nodes.Where(kv => kv.Value.RxNodes.Values.Any(n => n.Current)).OrderBy(a => a.Value.Name))
        {
            var txNs = Settings(txId);
            var txM = c.Matrix.GetOrAdd(tx.Name ?? txId);
            foreach (var (rxId, rx) in tx.RxNodes.Where(a => a.Value.Current).OrderBy(a => a.Value.Rx?.Name))
            {
                var rxNs = Settings(rxId);
                Put(txM, rx.Rx?.Name ?? rxId, new CalibrationCell(
                    txNs.Calibration.TxRefRssi, rxNs.Calibration.RxAdjRssi, rxNs.Calibration.Absorption,
                    rx.MapDistance, rx.Distance, rx.Rssi, rx.DistVar));

                if (rx.MapDistance > 0 && rx.Distance > 0)
                {
                    mapDistances.Add(rx.MapDistance);
                    actualDistances.Add(rx.Distance);
                }
            }
        }

        // Process Anchored Devices as Transmitters
        foreach (var device in state.Devices.Values.Where(d => d.IsAnchored && d.Anchor != null && d.Nodes.Values.Any(dn => dn.Current)))
        {
            var anchorName = device.Name ?? device.Id;
            c.Anchored.Add(anchorName);
            var txM = c.Matrix.GetOrAdd($"{anchorName}");

            foreach (var (rxId, deviceNode) in device.Nodes.Where(dn => dn.Value.Current && dn.Value.Node?.HasLocation == true))
            {
                var rxNode = deviceNode.Node!;
                var rxNs = Settings(rxNode.Id);
                var mapDistance = device.Anchor!.Location.DistanceTo(rxNode.Location);

                // Anchored devices don't have tx calibration settings, but receivers still have their settings
                Put(txM, rxNode.Name ?? rxNode.Id, new CalibrationCell(
                    null, rxNs.Calibration.RxAdjRssi, rxNs.Calibration.Absorption,
                    mapDistance, deviceNode.Distance, deviceNode.Rssi, deviceNode.DistVar));

                if (mapDistance > 0 && deviceNode.Distance > 0)
                {
                    mapDistances.Add(mapDistance);
                    actualDistances.Add(deviceNode.Distance);
                }
            }
        }

        c.R = MathUtils.CalculatePearsonCorrelation(mapDistances, actualDistances);
        c.RMSE = MathUtils.CalculateRMSE(mapDistances, actualDistances);
        // A handful of pairs can fit almost perfectly by chance, and a best-seen value sticks until reset.
        (c.BestRMSE, c.BestR) = mapDistances.Count >= MinPairsForBestMetrics
            ? state.RecordCalibrationMetrics(c.RMSE, c.R)
            : state.RecordCalibrationMetrics(null, null);
        return c;
    }

    /// <summary>Stores a cell; two receivers with the same name share one cell, as they always have.</summary>
    private static void Put(Dictionary<string, CalibrationCell> row, string rxName, CalibrationCell cell)
        => row[rxName] = cell.MergeOver(row.GetValueOrDefault(rxName));
}
