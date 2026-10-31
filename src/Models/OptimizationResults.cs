using ESPresense.Services;
using ESPresense.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Models;

/// <summary>
/// Outcome of <see cref="OptimizationResults.Evaluate"/>: fit quality plus the number of measurements it was computed from,
/// so callers can refuse to act on a fit that is only supported by a handful of samples.
/// </summary>
public readonly record struct OptimizationEvaluation(double Correlation, double RMSE, int Samples);

public class OptimizationResults
{
    public Dictionary<string, ProposedValues> Nodes { get; set; } = new();

    /// <summary>
    /// Rounds the proposal to the precision that is actually applied (absorption to 2 decimals, rxAdj and txRef
    /// to whole dB, matching <see cref="NodeSettingsStore"/>), so a proposal is scored as the value the node
    /// will really get rather than a fractional one that is never applied.
    /// </summary>
    public OptimizationResults QuantizeForApplication() => new()
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

    /// <summary>
    /// Scores this proposal against <paramref name="oss"/>. With <paramref name="deviceFrame"/> the
    /// proposed txRef values are ignored in favour of the transmitters' current ones: that is how a
    /// device is ranged (with its own rssi@1m, which no optimizer touches), so it is the frame in
    /// which rxAdj has to be right in absolute terms. Node-to-node data cannot tell "every
    /// transmitter k dB hot" from "every receiver k dB hot"; scoring in both frames can.
    /// </summary>
    public OptimizationEvaluation Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss, bool deviceFrame = false)
        => Evaluate(oss, nss.Get, deviceFrame);

    /// <summary>
    /// Same as above, but node settings come from <paramref name="getSettings"/>. A cycle passes a view frozen
    /// when it started, so every proposal is scored against the same incumbent however the store changes
    /// (for example when an earlier cycle's MQTT writes are echoed back) while the cycle runs.
    /// </summary>
    public OptimizationEvaluation Evaluate(List<OptimizationSnapshot> oss, Func<string, NodeSettings> getSettings, bool deviceFrame = false)
    {
        List<double> predictedValues = new();
        List<double> measuredValues = new();

        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                if (m.Tx?.Id == null || m.Rx?.Id == null) continue;
                var tx = getSettings(m.Tx.Id);
                var rx = getSettings(m.Rx.Id);

                Nodes.TryGetValue(m.Tx.Id, out var txPv);
                Nodes.TryGetValue(m.Rx.Id, out var rxPv);

                if (m.Rx?.Location == null || m.Tx?.Location == null)
                    continue;

                double mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);
                // Log10(0) is -Infinity and Log10(NaN) is NaN, which would poison the RMSE/correlation
                // for the whole evaluation; skip degenerate measures as IsotonicRegressionOptimizer does.
                if (mapDistance <= 0 || double.IsNaN(mapDistance) || double.IsInfinity(mapDistance))
                    continue;

                double rxAdjRssi = rxPv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? 0;
                double txRefRssi = (deviceFrame ? null : txPv?.TxRefRssi) ?? tx.Calibration.TxRefRssi ?? -59;
                double pathLossExponent = rxPv?.Absorption ?? rx.Calibration.Absorption ?? 2.7;

                double predictedRssi = txRefRssi - 10 * pathLossExponent * Math.Log10(mapDistance);
                double measuredRssi = m.GetAdjustedRssi(rxAdjRssi);

                predictedValues.Add(predictedRssi);
                measuredValues.Add(measuredRssi);
            }
        }

        var correlation = MathUtils.CalculatePearsonCorrelation(predictedValues, measuredValues);
        var rmse = MathUtils.CalculateRMSE(predictedValues, measuredValues);

        return new OptimizationEvaluation(correlation, rmse, predictedValues.Count);
    }
}
