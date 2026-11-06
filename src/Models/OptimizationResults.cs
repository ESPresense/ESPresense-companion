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
public readonly record struct OptimizationEvaluation(double Correlation, double RMSE, int Samples, double DistanceRmse = double.NaN);

/// <summary>One measure as <see cref="OptimizationResults.Predict"/> scores it: modelled and measured RSSI in dB.</summary>
internal readonly record struct PredictedMeasure(Measure Measure, double Predicted, double Measured, double MapDistance, double TxRefRssi, double PathLossExponent)
{
    /// <summary>Measured minus predicted: positive when the link is louder than the model expects.</summary>
    public double Residual => Measured - Predicted;
}

public class OptimizationResults
{
    // The scoring defaults for a node with no setting. Absorption and txRef match the firmware's own defaults
    // (DEFAULT_ABSORPTION and DEFAULT_TX_REF_RSSI in ESPresense main/defaults.h), which is what an unset node
    // actually runs with; keep them in step if the firmware changes.

    /// <summary>What a node with no absorption setting is scored with (firmware DEFAULT_ABSORPTION).</summary>
    internal const double DefaultAbsorption = 2.7;
    /// <summary>What a receiver with no rxAdj setting is scored with.</summary>
    internal const double DefaultRxAdjRssi = 0;
    /// <summary>What a node transmitter with no txRef setting is scored with (firmware DEFAULT_TX_REF_RSSI).</summary>
    internal const double DefaultTxRefRssi = -59;

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
    /// Anchored devices are scored with their rssi@1m (<see cref="Measure.HeldTxRef"/>) in both frames,
    /// as the optimizers fit them.
    /// </summary>
    public OptimizationEvaluation Evaluate(List<OptimizationSnapshot> oss, NodeSettingsStore nss, bool deviceFrame = false)
        => Evaluate(oss, nss.Get, deviceFrame);

    /// <summary>
    /// Same as above, but node settings come from <paramref name="getSettings"/>. A cycle passes a view frozen
    /// when it started, so every proposal is scored against the same incumbent however the store changes
    /// (for example when an earlier cycle's MQTT writes are echoed back) while the cycle runs.
    /// </summary>
    public OptimizationEvaluation Evaluate(List<OptimizationSnapshot> oss, Func<string, NodeSettings> getSettings, bool deviceFrame = false)
        => Score(Predict(oss, getSettings, deviceFrame).ToList());

    /// <summary>
    /// Pearson R and RMSE of predicted against measured RSSI over <paramref name="predictions"/>: the reduction
    /// <see cref="Evaluate(List{OptimizationSnapshot}, Func{string, NodeSettings}, bool)"/> applies to <see cref="Predict"/>.
    /// Lets a caller that also needs the per-measure residuals predict once and score from the same list.
    /// </summary>
    internal static OptimizationEvaluation Score(IReadOnlyList<PredictedMeasure> predictions)
    {
        var predictedValues = predictions.Select(p => p.Predicted).ToList();
        var measuredValues = predictions.Select(p => p.Measured).ToList();

        var correlation = MathUtils.CalculatePearsonCorrelation(predictedValues, measuredValues);
        var rmse = MathUtils.CalculateRMSE(predictedValues, measuredValues);

        // Distance RMSE (issue #1868): the distance each link's measured RSSI implies under the scored
        // calibration, d = 10^((txRef - measured) / (10 n)), against its map distance. A common shift of
        // every txRef or rxAdj (the gauge) leaves Pearson R where it was but moves these distances, and
        // distance is what the locator ranges with. Reported only; it is not part of the composite.
        var distanceErrors = predictions
            .Where(p => p.PathLossExponent > 0)
            .Select(p => Math.Pow(10.0, (p.TxRefRssi - p.Measured) / (10.0 * p.PathLossExponent)) - p.MapDistance)
            .Where(double.IsFinite)
            .ToList();
        var distanceRmse = distanceErrors.Count > 0 ? Math.Sqrt(distanceErrors.Average(e => e * e)) : double.NaN;

        return new OptimizationEvaluation(correlation, rmse, predictedValues.Count, distanceRmse);
    }

    /// <summary>
    /// The RSSI this proposal predicts for each usable measure in <paramref name="oss"/>, next to the RSSI it
    /// would have been measured at with the proposed rxAdj. <see cref="Evaluate(List{OptimizationSnapshot}, Func{string, NodeSettings}, bool)"/>
    /// scores exactly these pairs; the optimizer diagnostics report their residuals (measured minus predicted) per node.
    /// </summary>
    internal IEnumerable<PredictedMeasure> Predict(IEnumerable<OptimizationSnapshot> oss, Func<string, NodeSettings> getSettings, bool deviceFrame = false)
    {
        foreach (var os in oss)
        {
            foreach (var m in os.Measures)
            {
                if (m.Tx?.Id == null || m.Rx?.Id == null) continue;
                if (m.IsUnusableAnchor) continue; // no rssi@1m to score it with; see Measure.HeldTxRef
                var tx = getSettings(m.Tx.Id);
                var rx = getSettings(m.Rx.Id);

                Nodes.TryGetValue(m.Tx.Id, out var txPv);
                Nodes.TryGetValue(m.Rx.Id, out var rxPv);

                if (m.Rx?.Location == null || m.Tx?.Location == null)
                    continue;

                double mapDistance = m.Rx.Location.DistanceTo(m.Tx.Location);
                // Log10(0) is -Infinity and Log10(NaN) is NaN, which would poison the RMSE/correlation
                // for the whole evaluation; skip degenerate measures.
                if (mapDistance <= 0 || double.IsNaN(mapDistance) || double.IsInfinity(mapDistance))
                    continue;

                double rxAdjRssi = rxPv?.RxAdjRssi ?? rx.Calibration.RxAdjRssi ?? DefaultRxAdjRssi;
                // An anchored device is ranged with its own rssi@1m in either frame; nothing proposes or stores a txRef for it.
                double txRefRssi = m.HeldTxRef ?? (deviceFrame ? null : txPv?.TxRefRssi) ?? tx.Calibration.TxRefRssi ?? DefaultTxRefRssi;
                double pathLossExponent = rxPv?.Absorption ?? rx.Calibration.Absorption ?? DefaultAbsorption;

                double predictedRssi = txRefRssi - 10 * pathLossExponent * Math.Log10(mapDistance);
                double measuredRssi = m.GetAdjustedRssi(rxAdjRssi);

                yield return new PredictedMeasure(m, predictedRssi, measuredRssi, mapDistance, txRefRssi, pathLossExponent);
            }
        }
    }
}
