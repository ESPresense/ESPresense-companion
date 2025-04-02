using ESPresense.Services;
using Serilog;
using System;
using System.Collections.Generic;

namespace ESPresense.Models;

public class OptimizationResults
{
    // Parameter to control asymmetric error weighting - same as in the optimizer
    private const double AsymmetricErrorFactor = 5;

    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    public double Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss)
    {
        double squaredErrorSum = 0;
        double weightSum = 0;

        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                var tx = nss.Get(m.Tx.Id);
                var rx = nss.Get(m.Rx.Id);

                // Get proposed values for both TX and RX nodes (if available)
                Nodes.TryGetValue(m.Tx.Id, out var txPv);
                Nodes.TryGetValue(m.Rx.Id, out var rxPv);

                // Use inverse variance weighting - measurements with higher variance get lower weight
                double weight = 1.0;
                if (m.RssiVar > 0) // Ensure we don't divide by zero
                {
                    // Use inverse variance for weighting
                    weight = 1.0 / Math.Max(m.RssiVar.Value, 0.1); // Adding a minimum to avoid extreme weights
                }

                // Use proposed adjustments if available, otherwise fall back to calibration settings
                double rxAdjRssi = rxPv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? 0;
                double txRefRssi = txPv?.TxRefRssi ?? tx.Calibration.TxRefRssi ?? -59;
                double pathLossExponent = rxPv?.Absorption ?? rx.Calibration.Absorption ?? 2.7;
                double mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);

                // Calculate predicted RSSI based on the path loss model
                double predictedRssi = txRefRssi + rxAdjRssi - 10 * pathLossExponent * Math.Log10(mapDistance);

                // Calculate the expected distance based on measured RSSI
                // This is the distance the device would be at given the measured RSSI
                double calculatedDistance = Math.Pow(10, (txRefRssi + rxAdjRssi - m.Rssi) / (10.0 * pathLossExponent));

                // Apply asymmetric error handling based on physical constraints
                double error;
                if (calculatedDistance < mapDistance)
                {
                    // This case is physically impossible (can't be closer than map distance)
                    // Apply higher penalty with asymmetric factor
                    error = Math.Pow(predictedRssi - m.Rssi, 4);
                }
                else
                {
                    // Regular error calculation for the normal case (expected >= actual)
                    // This means there could be an obstacle causing signal attenuation
                    error = Math.Pow(predictedRssi - m.Rssi, 2);
                }

                // Apply weight to the error
                squaredErrorSum += weight * error;
                weightSum += weight;
            }
        }

        // Return weighted RMSE, or maximum value if no valid measurements
        return weightSum > 0 ? Math.Sqrt(squaredErrorSum / weightSum) : double.MaxValue;
    }
}