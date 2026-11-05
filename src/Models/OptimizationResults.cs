using ESPresense.Optimizers;
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

/// <summary>One measure as <see cref="OptimizationResults.Predict"/> scores it: modelled and measured RSSI in dB.</summary>
internal readonly record struct PredictedMeasure(Measure Measure, double Predicted, double Measured)
{
    /// <summary>Measured minus predicted: positive when the link is louder than the model expects.</summary>
    public double Residual => Measured - Predicted;
}

/// <summary>
/// Outcome of <see cref="OptimizationResults.EvaluateDevices"/>: how well a calibration places located devices.
/// </summary>
/// <param name="DeviceRmse">Mean |leave-one-out residual| in dB: how far each node's reading of a device is from what the fix made without it predicts.</param>
/// <param name="Scale">Mean solver scale (fitted / measured distance); a right calibration has scale near 1.</param>
/// <param name="Windows">Device windows scored.</param>
public readonly record struct DeviceEvaluation(double DeviceRmse, double Scale, int Windows)
{
    public static DeviceEvaluation None => new(double.NaN, double.NaN, 0);
    public bool HasData => Windows > 0 && double.IsFinite(DeviceRmse);
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

    /// <summary>Per-device rssi@1m learned by the device_residual optimizer; applied on their own guards, not the composite.</summary>
    public Dictionary<string, ProposedDeviceValues> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the device_residual optimizer learned this cycle (residual and scale before/after, per-node trust); null for other optimizers.</summary>
    public DeviceResidualState? DeviceResidual { get; set; }

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
                Error = pair.Value.Error,
                DeviceResidualVariance = pair.Value.DeviceResidualVariance
            }),
        Devices = Devices,
        DeviceResidual = DeviceResidual
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

        return new OptimizationEvaluation(correlation, rmse, predictedValues.Count);
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

                yield return new PredictedMeasure(m, predictedRssi, measuredRssi);
            }
        }
    }

    /// <summary>
    /// Scores this proposal on the located devices in <paramref name="oss"/>: re-solves every device window with
    /// the proposed rxAdj and absorption (the incumbent's where the proposal has none) and averages each node's
    /// |leave-one-out residual|. Node-to-node scoring cannot see what a device-facing rxAdj fixes; this can.
    /// Each window keeps the rssi@1m it was ranged with, so like the device frame of <see cref="Evaluate(List{OptimizationSnapshot}, Func{string, NodeSettings}, bool)"/>
    /// it judges rxAdj in absolute terms, and txRef proposals do not enter at all. Every node gets the same solver
    /// weight, as in the locators. Returns the total and the score of each snapshot's windows on their own.
    /// </summary>
    public (DeviceEvaluation Total, DeviceEvaluation[] PerSnapshot) EvaluateDevices(IReadOnlyList<OptimizationSnapshot> oss, Func<string, NodeSettings> getSettings, int minNodes)
    {
        var perSnapshot = Enumerable.Repeat(DeviceEvaluation.None, oss.Count).ToArray();
        var geometry = oss.LastOrDefault(s => s.Geometry != null && s.DeviceWindows.Count > 0)?.Geometry;
        if (geometry == null) return (DeviceEvaluation.None, perSnapshot);

        var solver = DeviceGridSolver.For(geometry);
        var windows = DeviceResidualModel.Prepare(oss.SelectMany((s, k) => s.DeviceWindows.Select(w => (w, k))), solver, minNodes, _ => 0);
        if (windows.Count == 0) return (DeviceEvaluation.None, perSnapshot);

        var count = solver.NodeIds.Count;
        var rxAdj = new double[count];
        var absorption = new double[count];
        var weights = Enumerable.Repeat(1.0, count).ToArray();
        for (int n = 0; n < count; n++)
        {
            var id = solver.NodeIds[n];
            Nodes.TryGetValue(id, out var pv);
            var settings = getSettings(id);
            rxAdj[n] = pv?.RxAdjRssi ?? settings.Calibration.RxAdjRssi ?? 0;
            absorption[n] = pv?.Absorption ?? settings.Calibration.Absorption ?? DeviceResidualModel.DefaultAbsorption;
        }

        var pass = DeviceResidualModel.Run(solver, windows, rxAdj, absorption, w => w.Source.RefRssi, weights, leaveOneOut: true);

        var sumAbs = new double[oss.Count];
        var residuals = new long[oss.Count];
        var sumScale = new double[oss.Count];
        var scored = new int[oss.Count];
        for (int i = 0; i < windows.Count; i++)
        {
            var k = windows[i].Group;
            foreach (var r in pass.LooResidual![i])
            {
                sumAbs[k] += Math.Abs(r);
                residuals[k]++;
            }
            sumScale[k] += Math.Pow(10, pass.LogScale[i]);
            scored[k]++;
        }

        for (int k = 0; k < oss.Count; k++)
            if (scored[k] > 0)
                perSnapshot[k] = new DeviceEvaluation(sumAbs[k] / residuals[k], sumScale[k] / scored[k], scored[k]);
        var total = new DeviceEvaluation(sumAbs.Sum() / residuals.Sum(), sumScale.Sum() / scored.Sum(), scored.Sum());
        return (total, perSnapshot);
    }
}
