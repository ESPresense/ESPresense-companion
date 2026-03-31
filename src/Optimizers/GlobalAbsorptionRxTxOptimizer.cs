using ESPresense.Models;
using ESPresense.Utils;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class GlobalAbsorptionRxTxOptimizer : IOptimizer
{
    private readonly State _state;

    private const double AsymmetricErrorFactor = 5;
    private const double CappedError = 2560;

    public GlobalAbsorptionRxTxOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Global Absorption Rx Adj + Antenna";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;
        if (optimization == null) return or;

        var allMeasures = os.ByRx().SelectMany(g => g).ToList();
        if (allMeasures.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization. Found {Count}, need 3+.", allMeasures.Count);
            return or;
        }

        var uniqueRxIds = allMeasures.Select(m => m.Rx.Id).Distinct().ToList();
        var uniqueTxIds = allMeasures.Select(m => m.Tx.Id).Distinct().ToList();
        var directionalNodeIds = allMeasures
            .SelectMany(m => new[] { m.Rx, m.Tx })
            .Where(n => n.IsNode && n.HasDirectionalAntenna)
            .Select(n => n.Id)
            .Distinct()
            .ToList();

        var weights = PreCalculateWeights(allMeasures);

        // Phase 1: Global absorption + per-Rx rxAdj + per-Tx txRef (isotropic, no gain)
        Phase1_IsotropicRxAdj(or, allMeasures, uniqueRxIds, uniqueTxIds, weights, optimization, existingSettings);

        // Phase 2: Antenna angles only, holding absorption + rxAdj + txRef fixed
        if (directionalNodeIds.Count > 0 && or.Nodes.Count > 0)
            Phase2_AntennaAngles(or, allMeasures, directionalNodeIds, weights, optimization, existingSettings);

        return or;
    }

    /// <summary>
    /// Phase 1: Standard isotropic optimization of global absorption, per-Rx rxAdjRssi, per-Tx txRefRssi.
    /// No antenna gain correction — proven convergence path.
    /// </summary>
    private void Phase1_IsotropicRxAdj(OptimizationResults or, List<Measure> allMeasures,
        List<string> uniqueRxIds, List<string> uniqueTxIds,
        Dictionary<Measure, double> weights, ConfigOptimization optimization,
        Dictionary<string, NodeSettings> existingSettings)
    {
        var rxIndexMap = new Dictionary<string, int>();
        var txIndexMap = new Dictionary<string, int>();
        int paramIndex = 0;
        int absorptionIdx = paramIndex++;
        foreach (var rxId in uniqueRxIds) rxIndexMap[rxId] = paramIndex++;
        foreach (var txId in uniqueTxIds) txIndexMap[txId] = paramIndex++;
        int totalParams = paramIndex;

        var obj = ObjectiveFunction.Gradient(
            x =>
            {
                double absorption = x[absorptionIdx];
                double squaredErrorSum = 0, weightSum = 0;

                foreach (var m in allMeasures)
                {
                    if (m.Rx?.Location == null || m.Tx?.Location == null) continue;
                    double rxAdj = x[rxIndexMap[m.Rx.Id]];
                    double txRef = x[txIndexMap[m.Tx.Id]];
                    double mapDist = Math.Max(m.Rx.Location.DistanceTo(m.Tx.Location), 0.1);

                    double predicted = txRef - 10 * absorption * Math.Log10(mapDist);
                    double diff = predicted - m.GetAdjustedRssi(rxAdj);

                    double w = weights[m];
                    weightSum += w;
                    double error = diff < 0
                        ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                        : Math.Min(diff * diff, CappedError);
                    squaredErrorSum += w * error;
                }
                return weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum;
            },
            x =>
            {
                var grad = Vector<double>.Build.Dense(totalParams);
                const double h = 1e-6;
                for (int i = 0; i < totalParams; i++)
                {
                    var xPlus = x.Clone(); xPlus[i] += h;
                    var xMinus = x.Clone(); xMinus[i] -= h;
                    grad[i] = (((Func<Vector<double>, double>)(v =>
                    {
                        double absorption = v[absorptionIdx];
                        double se = 0, ws = 0;
                        foreach (var m in allMeasures)
                        {
                            if (m.Rx?.Location == null || m.Tx?.Location == null) continue;
                            double rxAdj = v[rxIndexMap[m.Rx.Id]];
                            double txRef = v[txIndexMap[m.Tx.Id]];
                            double mapDist = Math.Max(m.Rx.Location.DistanceTo(m.Tx.Location), 0.1);
                            double predicted = txRef - 10 * absorption * Math.Log10(mapDist);
                            double diff = predicted - m.GetAdjustedRssi(rxAdj);
                            double w = weights[m]; ws += w;
                            double err = diff < 0 ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError) : Math.Min(diff * diff, CappedError);
                            se += w * err;
                        }
                        return ws > 0 ? se / ws : se;
                    }))(xPlus) - ((Func<Vector<double>, double>)(v =>
                    {
                        double absorption = v[absorptionIdx];
                        double se = 0, ws = 0;
                        foreach (var m in allMeasures)
                        {
                            if (m.Rx?.Location == null || m.Tx?.Location == null) continue;
                            double rxAdj = v[rxIndexMap[m.Rx.Id]];
                            double txRef = v[txIndexMap[m.Tx.Id]];
                            double mapDist = Math.Max(m.Rx.Location.DistanceTo(m.Tx.Location), 0.1);
                            double predicted = txRef - 10 * absorption * Math.Log10(mapDist);
                            double diff = predicted - m.GetAdjustedRssi(rxAdj);
                            double w = weights[m]; ws += w;
                            double err = diff < 0 ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError) : Math.Min(diff * diff, CappedError);
                            se += w * err;
                        }
                        return ws > 0 ? se / ws : se;
                    }))(xMinus)) / (2 * h);
                }
                return grad;
            }
        );

        var lower = Vector<double>.Build.Dense(totalParams);
        var upper = Vector<double>.Build.Dense(totalParams);
        var initial = Vector<double>.Build.Dense(totalParams);

        lower[absorptionIdx] = optimization.AbsorptionMin;
        upper[absorptionIdx] = optimization.AbsorptionMax;
        var validAbsorptions = existingSettings.Values
            .Select(ns => ns.Calibration?.Absorption).Where(a => a.HasValue).Select(a => a!.Value).ToList();
        initial[absorptionIdx] = Math.Clamp(
            validAbsorptions.Any() ? validAbsorptions.Average() : (optimization.AbsorptionMin + optimization.AbsorptionMax) / 2.0,
            optimization.AbsorptionMin, optimization.AbsorptionMax);

        foreach (var rxId in uniqueRxIds)
        {
            int idx = rxIndexMap[rxId];
            lower[idx] = optimization.RxAdjRssiMin;
            upper[idx] = optimization.RxAdjRssiMax;
            existingSettings.TryGetValue(rxId, out var ns);
            initial[idx] = Math.Clamp(ns?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }
        foreach (var txId in uniqueTxIds)
        {
            int idx = txIndexMap[txId];
            lower[idx] = optimization.TxRefRssiMin;
            upper[idx] = optimization.TxRefRssiMax;
            existingSettings.TryGetValue(txId, out var ns);
            initial[idx] = Math.Clamp(ns?.Calibration?.TxRefRssi ?? -59, optimization.TxRefRssiMin, optimization.TxRefRssiMax);
        }

        try
        {
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(obj, lower, upper, initial);

            double absorption = Math.Clamp(result.MinimizingPoint[absorptionIdx], optimization.AbsorptionMin, optimization.AbsorptionMax);
            foreach (var rxId in uniqueRxIds)
            {
                double rxAdj = Math.Clamp(result.MinimizingPoint[rxIndexMap[rxId]], optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
                var n = or.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdj;
                n.Absorption = absorption;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }
            foreach (var txId in uniqueTxIds)
            {
                double txRef = Math.Clamp(result.MinimizingPoint[txIndexMap[txId]], optimization.TxRefRssiMin, optimization.TxRefRssiMax);
                var n = or.Nodes.GetOrAdd(txId);
                n.TxRefRssi = txRef;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            Log.Debug("{Name} Phase 1 completed with error: {Error}, absorption: {Absorption}", Name, result.FunctionInfoAtMinimum.Value, absorption);
        }
        catch (Exception ex)
        {
            Log.Error("Error in {Name} Phase 1: {Message}", Name, ex.Message);
        }
    }

    /// <summary>
    /// Phase 2: Optimize antenna angles only. Holds absorption/rxAdj/txRef fixed from Phase 1.
    /// Uses Tx+Rx gain correction. Output angles quantized to 45°.
    /// </summary>
    private void Phase2_AntennaAngles(OptimizationResults or, List<Measure> allMeasures,
        List<string> directionalNodeIds, Dictionary<Measure, double> weights,
        ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        // Build fixed parameter lookups from Phase 1
        var fixedRxAdj = new Dictionary<string, double>();
        var fixedTxRef = new Dictionary<string, double>();
        double fixedAbsorption = 3.0;
        foreach (var (id, pv) in or.Nodes)
        {
            if (pv.RxAdjRssi != null) fixedRxAdj[id] = pv.RxAdjRssi.Value;
            if (pv.TxRefRssi != null) fixedTxRef[id] = pv.TxRefRssi.Value;
            if (pv.Absorption != null) fixedAbsorption = pv.Absorption.Value;
        }

        // Parameters: [sinAz_0, cosAz_0, sinEl_0, sinAz_1, cosAz_1, sinEl_1, ...]
        int N = directionalNodeIds.Count;
        int totalParams = N * 3;
        var idxMap = new Dictionary<string, int>();
        for (int i = 0; i < N; i++)
            idxMap[directionalNodeIds[i]] = i * 3;

        double EvalPhase2(double[] xa)
        {
            double squaredErrorSum = 0, weightSum = 0, penalty = 0;

            // Unit-circle regularization
            for (int i = 0; i < N; i++)
            {
                double sa = xa[i * 3], ca = xa[i * 3 + 1];
                double dev = sa * sa + ca * ca - 1.0;
                penalty += 0.1 * dev * dev;
            }

            foreach (var m in allMeasures)
            {
                if (m.Rx?.Location == null || m.Tx?.Location == null) continue;

                double rxAdj = fixedRxAdj.GetValueOrDefault(m.Rx.Id, 0);
                double txRef = fixedTxRef.GetValueOrDefault(m.Tx.Id, -59);
                double mapDist = Math.Max(m.Rx.Location.DistanceTo(m.Tx.Location), 0.1);

                double gainDb = ComputeLinkGainDb(xa, idxMap, m);
                if (double.IsNaN(gainDb) || double.IsInfinity(gainDb)) continue;

                double predicted = txRef + gainDb - 10 * fixedAbsorption * Math.Log10(mapDist);
                double diff = predicted - m.GetAdjustedRssi(rxAdj);

                double w = weights[m];
                weightSum += w;
                double error = diff < 0
                    ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                    : Math.Min(diff * diff, CappedError);
                squaredErrorSum += w * error;
            }

            return (weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum) + penalty;
        }

        var obj = ObjectiveFunction.Gradient(
            x => EvalPhase2(x.ToArray()),
            x =>
            {
                var xa = x.ToArray();
                var grad = Vector<double>.Build.Dense(totalParams);
                const double h = 1e-5;
                double baseVal = EvalPhase2(xa);
                for (int i = 0; i < totalParams; i++)
                {
                    var xp = (double[])xa.Clone();
                    xp[i] += h;
                    grad[i] = (EvalPhase2(xp) - baseVal) / h;
                }
                return grad;
            }
        );

        var lower = new double[totalParams];
        var upper = new double[totalParams];
        var init = new double[totalParams];
        for (int i = 0; i < N; i++)
        {
            existingSettings.TryGetValue(directionalNodeIds[i], out var ns);
            double azRad, elRad;
            if (ns?.Calibration?.Azimuth != null && ns?.Calibration?.Elevation != null)
            {
                azRad = ns.Calibration.Azimuth.Value * Math.PI / 180.0;
                elRad = ns.Calibration.Elevation.Value * Math.PI / 180.0;
            }
            else
            {
                // Seed toward centroid of other nodes
                var thisNode = allMeasures.Select(m => m.Rx.Id == directionalNodeIds[i] ? m.Rx : m.Tx.Id == directionalNodeIds[i] ? m.Tx : null).First(n => n != null)!;
                var others = allMeasures
                    .Where(m => m.Rx.Id == directionalNodeIds[i] || m.Tx.Id == directionalNodeIds[i])
                    .Select(m => m.Rx.Id == directionalNodeIds[i] ? m.Tx.Location : m.Rx.Location)
                    .ToList();
                double cx = others.Average(p => p.X) - thisNode.Location.X;
                double cy = others.Average(p => p.Y) - thisNode.Location.Y;
                double cz = others.Average(p => p.Z) - thisNode.Location.Z;
                double cLen = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                if (cLen < 1e-9) { azRad = 0; elRad = 0; }
                else
                {
                    azRad = Math.Atan2(cx, cy);
                    elRad = Math.Asin(Math.Clamp(cz / cLen, -1.0, 1.0));
                }
            }

            int b = i * 3;
            init[b] = Math.Sin(azRad); lower[b] = -2.0; upper[b] = 2.0;
            init[b + 1] = Math.Cos(azRad); lower[b + 1] = -2.0; upper[b + 1] = 2.0;
            init[b + 2] = Math.Sin(elRad); lower[b + 2] = -1.0; upper[b + 2] = 1.0;
        }

        try
        {
            var solver = new BfgsBMinimizer(1e-7, 1e-7, 1e-7, 5000);
            var result = solver.FindMinimum(obj,
                Vector<double>.Build.DenseOfArray(lower),
                Vector<double>.Build.DenseOfArray(upper),
                Vector<double>.Build.DenseOfArray(init));

            var mp = result.MinimizingPoint.ToArray();
            for (int i = 0; i < N; i++)
            {
                int b = i * 3;
                double azRad = Math.Atan2(mp[b], mp[b + 1]);
                double elRad = Math.Asin(Math.Clamp(mp[b + 2], -1.0, 1.0));
                double azDeg = azRad * 180.0 / Math.PI;
                if (azDeg < 0) azDeg += 360.0;

                var n = or.Nodes.GetOrAdd(directionalNodeIds[i]);
                n.Azimuth = Math.Round(azDeg / 45.0) * 45.0 % 360.0;
                n.Elevation = Math.Round(elRad * 180.0 / Math.PI / 45.0) * 45.0;
            }

            Log.Debug("{Name} Phase 2 (antenna) completed with error: {Error}", Name, result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Error in {Name} Phase 2: {Message}", Name, ex.Message);
        }
    }

    private Dictionary<Measure, double> PreCalculateWeights(List<Measure> allMeasures)
    {
        var measureWeights = new Dictionary<Measure, double>();
        double totalWeight = 0;
        foreach (var m in allMeasures)
        {
            double w = m.RssiVar > 0 ? 1.0 / Math.Max(m.RssiVar.Value, 0.1) : 1.0;
            measureWeights[m] = w;
            totalWeight += w;
        }
        if (totalWeight > 0)
            foreach (var m in allMeasures)
                measureWeights[m] = measureWeights[m] / totalWeight * allMeasures.Count;
        return measureWeights;
    }

    private static double ComputeLinkGainDb(double[] x, Dictionary<string, int> idxMap, Measure measure)
    {
        double dx = measure.Tx.Location.X - measure.Rx.Location.X;
        double dy = measure.Tx.Location.Y - measure.Rx.Location.Y;
        double dz = measure.Tx.Location.Z - measure.Rx.Location.Z;
        return ComputeNodeGainDb(x, idxMap, measure.Rx, dx, dy, dz)
             + ComputeNodeGainDb(x, idxMap, measure.Tx, -dx, -dy, -dz);
    }

    private static double ComputeNodeGainDb(double[] x, Dictionary<string, int> idxMap, OptNode node, double dx, double dy, double dz)
    {
        if (!node.IsNode || !node.HasDirectionalAntenna || !idxMap.TryGetValue(node.Id, out var idx))
            return 0.0;

        double sinAz = x[idx], cosAz = x[idx + 1], sinEl = x[idx + 2];
        double azNorm = Math.Sqrt(sinAz * sinAz + cosAz * cosAz);
        if (azNorm < 1e-9) { sinAz = 0.0; cosAz = 1.0; }
        else { sinAz /= azNorm; cosAz /= azNorm; }
        double cosEl = Math.Sqrt(Math.Max(1.0 - sinEl * sinEl, 0.0));

        return MathUtils.ComputeGainDb(
            sinAz * cosEl, cosAz * cosEl, sinEl, dx, dy, dz,
            10.0 * Math.Log10(node.GMax), node.PatternExponent, node.BackLossDb);
    }
}
