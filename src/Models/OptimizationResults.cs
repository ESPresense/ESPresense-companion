using ESPresense.Services;
using ESPresense.Companion.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

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

                double predictedRssi = txRefRssi - 10 * pathLossExponent * Math.Log10(mapDistance);
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
