using ESPresense.Services;
using Serilog;
using System;
using System.Collections.Generic;

namespace ESPresense.Models;

public class OptimizationResults
{
    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    public double Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss)
    {
        List<double> predictedValues = new List<double>();
        List<double> measuredValues = new List<double>();

        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                var tx = nss.Get(m.Tx.Id);
                var rx = nss.Get(m.Rx.Id);

                Nodes.TryGetValue(m.Tx.Id, out var txPv);
                Nodes.TryGetValue(m.Rx.Id, out var rxPv);

                if (m.Rx?.Location == null || m.Tx?.Location == null)
                {
                    continue; // Skip this measurement if locations are missing
                }

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

        return CalculatePearsonCorrelation(predictedValues, measuredValues);
    }

    private double CalculatePearsonCorrelation(List<double> x, List<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
            return 0;

        double sumX = 0;
        double sumY = 0;
        double sumXY = 0;
        double sumX2 = 0;
        double sumY2 = 0;
        int n = x.Count;

        for (int i = 0; i < n; i++)
        {
            sumX += x[i];
            sumY += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
        }

        double numerator = n * sumXY - sumX * sumY;
        double denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));
        return (denominator != 0) ? numerator / denominator : 0;
    }
}