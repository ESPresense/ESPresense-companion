using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

/// <summary>
/// Joint fit of rxAdj + absorption per receiver and txRef per transmitter, by least squares on the
/// RSSI residual <c>measured(rxAdj) - (txRef - 10*absorption*log10(d))</c>.
///
/// Two things keep it honest on real radio:
///
/// Fitting in dB, not metres. The log-distance model's noise is roughly constant in dB, so a dB
/// residual weights every measurement alike; a metre residual is dominated by the far nodes, and an
/// asymmetric one (the previous quartic penalty on under-estimates) bends the whole fit toward
/// over-estimating. Huber loss caps the pull of multipath outliers.
///
/// Anchoring txRef. With only node-to-node data, "every transmitter is k dB hot" and "every
/// receiver is k dB hot" produce identical RSSI, so the data alone cannot place the calibration;
/// but devices are ranged with their own rssi@1m, which the optimizer never touches, so the choice
/// matters to them: rxAdj must be right in absolute terms. The anchor is the reference the
/// transmitter already advertises (its configured txRef, else the rssi@1m on the measurement), and
/// txRef only moves off it when the evidence outweighs the prior.
/// </summary>
public class PerNodeAbsorptionRxTx : IOptimizer
{
    private readonly State _state;

    /// <summary>Residuals beyond this many dB count linearly rather than quadratically.</summary>
    private const double HuberDb = 6;

    /// <summary>Prior weight on absorption: a 0.5 deviation from the mid-limit costs as much as a 1 dB residual.</summary>
    private const double AbsorptionPrior = 4;

    /// <summary>Prior weight on txRef: a 2 dB departure from the advertised reference costs as much as a ~1.4 dB residual.</summary>
    private const double TxRefPrior = 0.5;

    public PerNodeAbsorptionRxTx(State state)
    {
        _state = state;
    }

    public string Name => "Per Node Absorption Rx Tx Adj";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;
        if (optimization == null) return or;

        var measures = os.Measures
            .Where(m => m.Rx?.Location != null && m.Tx?.Location != null)
            .Select(m => (m, logD: Math.Log10(m.Rx.Location.DistanceTo(m.Tx.Location))))
            .Where(t => double.IsFinite(t.logD))
            .ToList();

        if (measures.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization. Found {Count} measurements, need at least 3. Add more BLE beacons or ESPresense nodes.", measures.Count);
            return or;
        }

        var rxIds = measures.Select(t => t.m.Rx.Id).Distinct().ToList();
        var txIds = measures.Select(t => t.m.Tx.Id).Distinct().ToList();

        // Layout: [rxAdj, absorption] per receiver, then txRef per transmitter.
        var rxIndex = rxIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => 2 * t.i);
        var txIndex = txIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => 2 * rxIds.Count + t.i);
        int totalParams = 2 * rxIds.Count + txIds.Count;

        var midAbsorption = (optimization.AbsorptionMin + optimization.AbsorptionMax) / 2.0;

        // The reference each transmitter is held near: what it is already calibrated or advertised at.
        var txAnchor = txIds.ToDictionary(id => id, id =>
        {
            existingSettings.TryGetValue(id, out var ns);
            var configured = ns?.Calibration?.TxRefRssi;
            if (configured.HasValue) return (double)configured.Value;
            var refs = measures.Where(t => t.m.Tx.Id == id).Select(t => t.m.RefRssi).Where(double.IsFinite).ToList();
            return refs.Count > 0 ? refs.Average() : -59;
        });

        // Inverse-variance weights, normalised to mean 1 so the priors keep their meaning.
        var weights = measures.Select(t => t.m.RssiVar > 0 ? 1.0 / Math.Max(t.m.RssiVar.Value, 0.1) : 1.0).ToArray();
        var weightSum = weights.Sum();
        for (int i = 0; i < weights.Length; i++) weights[i] = weights[i] / weightSum * weights.Length;

        double Residual(Vector<double> x, int i)
        {
            var (m, logD) = measures[i];
            double rxAdj = x[rxIndex[m.Rx.Id]];
            double absorption = x[rxIndex[m.Rx.Id] + 1];
            double txRef = x[txIndex[m.Tx.Id]];
            return m.GetAdjustedRssi(rxAdj) - (txRef - 10 * absorption * logD);
        }

        static double Huber(double e) => Math.Abs(e) <= HuberDb ? 0.5 * e * e : HuberDb * (Math.Abs(e) - 0.5 * HuberDb);
        static double HuberPrime(double e) => Math.Abs(e) <= HuberDb ? e : HuberDb * Math.Sign(e);

        double Objective(Vector<double> x)
        {
            double loss = 0;
            for (int i = 0; i < measures.Count; i++)
                loss += weights[i] * Huber(Residual(x, i));
            loss /= measures.Count;

            foreach (var id in rxIds)
                loss += AbsorptionPrior / rxIds.Count * Math.Pow(x[rxIndex[id] + 1] - midAbsorption, 2);
            foreach (var id in txIds)
                loss += TxRefPrior / txIds.Count * Math.Pow(x[txIndex[id]] - txAnchor[id], 2);
            return loss;
        }

        Vector<double> Gradient(Vector<double> x)
        {
            var grad = Vector<double>.Build.Dense(totalParams);
            for (int i = 0; i < measures.Count; i++)
            {
                var (m, logD) = measures[i];
                double g = weights[i] * HuberPrime(Residual(x, i)) / measures.Count;
                int r = rxIndex[m.Rx.Id];
                // d(residual)/d(rxAdj) is -1 when the measurement carries the rxAdj it was taken with
                // (GetAdjustedRssi re-bases it), and 0 when it does not and rxAdj cannot be fitted.
                if (m.RssiRxAdj != null) grad[r] -= g;
                grad[r + 1] += g * 10 * logD;
                grad[txIndex[m.Tx.Id]] -= g;
            }
            foreach (var id in rxIds)
                grad[rxIndex[id] + 1] += 2 * AbsorptionPrior / rxIds.Count * (x[rxIndex[id] + 1] - midAbsorption);
            foreach (var id in txIds)
                grad[txIndex[id]] += 2 * TxRefPrior / txIds.Count * (x[txIndex[id]] - txAnchor[id]);
            return grad;
        }

        var lower = Vector<double>.Build.Dense(totalParams);
        var upper = Vector<double>.Build.Dense(totalParams);
        var initial = Vector<double>.Build.Dense(totalParams);
        foreach (var id in rxIds)
        {
            int r = rxIndex[id];
            existingSettings.TryGetValue(id, out var ns);
            lower[r] = optimization.RxAdjRssiMin; upper[r] = optimization.RxAdjRssiMax;
            lower[r + 1] = optimization.AbsorptionMin; upper[r + 1] = optimization.AbsorptionMax;
            initial[r] = Math.Clamp(ns?.Calibration?.RxAdjRssi ?? 0, lower[r], upper[r]);
            initial[r + 1] = Math.Clamp(ns?.Calibration?.Absorption ?? midAbsorption, lower[r + 1], upper[r + 1]);
        }
        foreach (var id in txIds)
        {
            int t = txIndex[id];
            lower[t] = optimization.TxRefRssiMin; upper[t] = optimization.TxRefRssiMax;
            initial[t] = Math.Clamp(txAnchor[id], lower[t], upper[t]);
        }

        try
        {
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(ObjectiveFunction.Gradient(Objective, Gradient), lower, upper, initial);
            var x = result.MinimizingPoint.Clone();
            for (int i = 0; i < totalParams; i++) x[i] = Math.Clamp(x[i], lower[i], upper[i]);

            // Each node's own dB residuals at the fitted values, rather than the global objective, which
            // is the same for every node and includes the priors.
            var rms = new NodeResidualRms();
            for (int i = 0; i < measures.Count; i++)
                rms.Add(measures[i].m, Residual(x, i), weights[i]);

            foreach (var id in rxIds)
            {
                var n = or.Nodes.GetOrAdd(id);
                n.RxAdjRssi = x[rxIndex[id]];
                n.Absorption = x[rxIndex[id] + 1];
                n.Error = rms.Get(id);
            }
            foreach (var id in txIds)
            {
                var n = or.Nodes.GetOrAdd(id);
                n.TxRefRssi = x[txIndex[id]];
                n.Error = rms.Get(id);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing all nodes: {0}", ex.Message);
        }

        return or;
    }
}
