using ESPresense.Services;
using ESPresense.Utils;
using System;

namespace ESPresense.Models;

public class OptimizationResults
{
    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    public (double Correlation, double RMSE) Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss)
    {
        List<double> predictedValues = new();
        List<double> measuredValues = new();

        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                if (m.Tx?.Id == null || m.Rx?.Id == null) continue;
                var tx = nss.Get(m.Tx.Id);
                var rx = nss.Get(m.Rx.Id);

                Nodes.TryGetValue(m.Tx.Id, out var txPv);
                Nodes.TryGetValue(m.Rx.Id, out var rxPv);

                if (m.Rx?.Location == null || m.Tx?.Location == null)
                    continue;

                double mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);

                double rxAdjRssi = rxPv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? 0;
                double txRefRssi = txPv?.TxRefRssi ?? tx.Calibration.TxRefRssi ?? -59;
                double pathLossExponent = rxPv?.Absorption ?? rx.Calibration.Absorption ?? 2.7;

                // Apply antenna gain correction for both Rx and Tx directional antennas.
                // For node-to-node paths, both antennas affect the signal.
                double gainDb = 0.0;
                double dx = m.Tx.Location.X - m.Rx.Location.X;
                double dy = m.Tx.Location.Y - m.Rx.Location.Y;
                double dz = m.Tx.Location.Z - m.Rx.Location.Z;
                if (m.Rx.HasDirectionalAntenna)
                {
                    double azRad = (rxPv?.Azimuth ?? rx.Calibration.Azimuth ?? 0.0) * Math.PI / 180.0;
                    double elRad = (rxPv?.Elevation ?? rx.Calibration.Elevation ?? 0.0) * Math.PI / 180.0;
                    double px = Math.Sin(azRad) * Math.Cos(elRad);
                    double py = Math.Cos(azRad) * Math.Cos(elRad);
                    double pz = Math.Sin(elRad);
                    gainDb += MathUtils.ComputeGainDb(px, py, pz, dx, dy, dz,
                        10.0 * Math.Log10(m.Rx.GMax), m.Rx.PatternExponent, m.Rx.BackLossDb);
                }
                if (m.Tx.HasDirectionalAntenna)
                {
                    Nodes.TryGetValue(m.Tx.Id, out var txPvLocal);
                    double azRad = (txPvLocal?.Azimuth ?? tx.Calibration.Azimuth ?? 0.0) * Math.PI / 180.0;
                    double elRad = (txPvLocal?.Elevation ?? tx.Calibration.Elevation ?? 0.0) * Math.PI / 180.0;
                    double px = Math.Sin(azRad) * Math.Cos(elRad);
                    double py = Math.Cos(azRad) * Math.Cos(elRad);
                    double pz = Math.Sin(elRad);
                    // Tx→Rx: direction vector is negated (Rx - Tx)
                    gainDb += MathUtils.ComputeGainDb(px, py, pz, -dx, -dy, -dz,
                        10.0 * Math.Log10(m.Tx.GMax), m.Tx.PatternExponent, m.Tx.BackLossDb);
                }

                double predictedRssi = txRefRssi + gainDb - 10 * pathLossExponent * Math.Log10(mapDistance);
                double measuredRssi = m.GetAdjustedRssi(rxAdjRssi);

                predictedValues.Add(predictedRssi);
                measuredValues.Add(measuredRssi);
            }
        }

        var correlation = MathUtils.CalculatePearsonCorrelation(predictedValues, measuredValues);
        var rmse = MathUtils.CalculateRMSE(predictedValues, measuredValues);

        return (correlation, rmse);
    }
}
