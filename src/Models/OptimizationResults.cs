using ESPresense.Services;
using ESPresense.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Models;

public class OptimizationResults
{
    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    public OptimizationResults QuantizeForApplication()
    {
        return new OptimizationResults
        {
            Nodes = Nodes.ToDictionary(
                pair => pair.Key,
                pair => new ProposedValues
                {
                    Absorption = pair.Value.Absorption is { } absorption ? Math.Round(absorption, 2) : null,
                    RxAdjRssi = pair.Value.RxAdjRssi is { } rxAdjRssi ? Math.Round(rxAdjRssi) : null,
                    TxRefRssi = pair.Value.TxRefRssi is { } txRefRssi ? Math.Round(txRefRssi) : null,
                    Error = pair.Value.Error
                })
        };
    }

    public (double Correlation, double RMSE) Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss)
    {
        var metrics = EvaluateMetrics(oss, nss);
        return (metrics.Correlation, metrics.Rmse);
    }

    public OptimizationMetrics EvaluateMetrics(
        IEnumerable<OptimizationSnapshot> snapshots,
        NodeSettingsStore nodeSettings,
        double huberDelta = 2.0)
    {
        List<double> predictedValues = new();
        List<double> measuredValues = new();

        foreach (var snapshot in snapshots)
        {
            foreach (var measure in snapshot.Measures)
            {
                if (measure.Tx?.Id == null || measure.Rx?.Id == null) continue;
                var tx = nodeSettings.Get(measure.Tx.Id);
                var rx = nodeSettings.Get(measure.Rx.Id);

                Nodes.TryGetValue(measure.Tx.Id, out var txPv);
                Nodes.TryGetValue(measure.Rx.Id, out var rxPv);

                if (measure.Rx?.Location == null || measure.Tx?.Location == null) continue;

                double mapDistance = measure.Rx.Location.DistanceTo(measure.Tx.Location);
                if (!double.IsFinite(mapDistance) || mapDistance <= 0) continue;

                double rxAdjRssi = rxPv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? 0;
                double txRefRssi = txPv?.TxRefRssi ?? tx.Calibration.TxRefRssi ?? -59;
                double pathLossExponent = rxPv?.Absorption ?? rx.Calibration.Absorption ?? 2.7;

                double predictedRssi = txRefRssi - 10 * pathLossExponent * Math.Log10(mapDistance);
                double measuredRssi = measure.GetAdjustedRssi(rxAdjRssi);

                if (!double.IsFinite(predictedRssi) || !double.IsFinite(measuredRssi)) continue;

                predictedValues.Add(predictedRssi);
                measuredValues.Add(measuredRssi);
            }
        }

        var correlation = MathUtils.CalculatePearsonCorrelation(predictedValues, measuredValues);
        var rmse = MathUtils.CalculateRMSE(predictedValues, measuredValues);
        var mae = predictedValues.Count == 0
            ? double.NaN
            : predictedValues.Zip(measuredValues, (predicted, measured) => Math.Abs(predicted - measured)).Average();
        var robustLoss = CalculateHuberLoss(predictedValues, measuredValues, huberDelta);

        return new OptimizationMetrics(predictedValues.Count, correlation, rmse, mae, robustLoss);
    }

    private static double CalculateHuberLoss(
        IReadOnlyList<double> predicted,
        IReadOnlyList<double> measured,
        double delta)
    {
        if (predicted.Count == 0 || predicted.Count != measured.Count) return double.NaN;

        delta = Math.Max(0.1, delta);
        double loss = 0;
        for (int i = 0; i < predicted.Count; i++)
        {
            var error = Math.Abs(predicted[i] - measured[i]);
            loss += error <= delta
                ? 0.5 * error * error
                : delta * (error - 0.5 * delta);
        }

        return loss / predicted.Count;
    }
}

public sealed record OptimizationMetrics(
    int SampleCount,
    double Correlation,
    double Rmse,
    double Mae,
    double HuberLoss)
{
    public bool IsValid => SampleCount > 0 &&
                           double.IsFinite(Rmse) &&
                           double.IsFinite(Mae) &&
                           double.IsFinite(HuberLoss);
}
