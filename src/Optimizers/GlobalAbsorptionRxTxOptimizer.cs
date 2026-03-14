using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class GlobalAbsorptionRxTxOptimizer : IOptimizer
{
    private readonly State _state;

    // Parameter to control asymmetric error weighting
    // Higher value means we penalize "impossible" cases more strongly
    private const double AsymmetricErrorFactor = 5;
    // Capped error value to prevent extreme outliers from skewing results
    private const double CappedError = 2560;

    public GlobalAbsorptionRxTxOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Global Absorption Rx Tx Adj";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        if (optimization == null) return or;

        // Group all valid measurements
        var allRxNodes = os.ByRx().SelectMany(g => g).ToList();

        if (allRxNodes.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization. Found {Count} measurements, need at least 3. Add more BLE beacons or ESPresense nodes.", allRxNodes.Count);
            return or;
        }

        // Get unique Rx and Tx nodes
        var uniqueRxIds = allRxNodes.Select(n => n.Rx.Id).Distinct().ToList();
        var uniqueTxIds = allRxNodes.Select(n => n.Tx.Id).Distinct().ToList();

        // Pre-calculate weights for each node based on RssiVar
        var nodeWeights = PreCalculateWeights(allRxNodes);

        // Phase 1: Optimize absorption, rxAdjRssi, txRefRssi (isotropic, proven path)
        Phase1_OptimizeAbsorptionRxTx(or, allRxNodes, uniqueRxIds, uniqueTxIds, nodeWeights, optimization, existingSettings);

        // Phase 2: Optimize antenna angles only, holding Phase 1 results fixed
        var directionalRxIds = uniqueRxIds.Where(id => allRxNodes.Any(m => m.Rx.Id == id && m.Rx.IsNode && m.Rx.HasDirectionalAntenna)).ToList();
        if (directionalRxIds.Count > 0)
            Phase2_OptimizeAntennaAngles(or, allRxNodes, directionalRxIds, nodeWeights, optimization, existingSettings);

        return or;
    }

    private Dictionary<Measure, double> PreCalculateWeights(List<Measure> allRxNodes)
    {
        var nodeWeights = new Dictionary<Measure, double>();
        double totalWeight = 0;

        foreach (var node in allRxNodes)
        {
            double weight = 1.0;
            if (node.RssiVar > 0)
                weight = 1.0 / Math.Max(node.RssiVar.Value, 0.1);
            nodeWeights[node] = weight;
            totalWeight += weight;
        }

        if (totalWeight > 0)
        {
            foreach (var node in allRxNodes)
                nodeWeights[node] = nodeWeights[node] / totalWeight * allRxNodes.Count;
        }

        return nodeWeights;
    }

    /// <summary>
    /// Phase 1: Standard isotropic optimization of global absorption, per-Rx rxAdjRssi, per-Tx txRefRssi.
    /// This is the proven optimization path that achieves good RMSE.
    /// </summary>
    private void Phase1_OptimizeAbsorptionRxTx(OptimizationResults or, List<Measure> allRxNodes,
        List<string> uniqueRxIds, List<string> uniqueTxIds,
        Dictionary<Measure, double> nodeWeights, ConfigOptimization optimization,
        Dictionary<string, NodeSettings> existingSettings)
    {
        var rxIndexMap = new Dictionary<string, int>();
        var txIndexMap = new Dictionary<string, int>();
        int paramIndex = 0;
        int globalAbsorptionIndex = paramIndex++;

        foreach (var rxId in uniqueRxIds)
            rxIndexMap[rxId] = paramIndex++;
        foreach (var txId in uniqueTxIds)
            txIndexMap[txId] = paramIndex++;

        int totalParams = paramIndex;

        var objectiveFunction = ObjectiveFunction.Gradient(
            x =>
            {
                double squaredErrorSum = 0;
                double weightSum = 0;
                double globalAbsorption = x[globalAbsorptionIndex];

                foreach (var node in allRxNodes)
                {
                    if (node.Rx?.Location == null || node.Tx?.Location == null) continue;

                    double rxAdjRssi = x[rxIndexMap[node.Rx.Id]];
                    double txRefRssi = x[txIndexMap[node.Tx.Id]];
                    double mapDistance = Math.Max(node.Rx.Location.DistanceTo(node.Tx.Location), 0.1);

                    double predictedRssi = txRefRssi - 10 * globalAbsorption * Math.Log10(mapDistance);
                    double diff = predictedRssi - node.GetAdjustedRssi(rxAdjRssi);

                    double weight = nodeWeights[node];
                    weightSum += weight;

                    double error = diff < 0
                        ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                        : Math.Min(diff * diff, CappedError);
                    squaredErrorSum += weight * error;
                }

                return weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum;
            },
            x =>
            {
                var grad = Vector<double>.Build.Dense(totalParams);
                double h = 1e-6;
                for (int i = 0; i < totalParams; i++)
                {
                    var xPlus = x.Clone(); xPlus[i] += h;
                    var xMinus = x.Clone(); xMinus[i] -= h;

                    double fPlus = EvalPhase1(xPlus, allRxNodes, globalAbsorptionIndex, rxIndexMap, txIndexMap, nodeWeights);
                    double fMinus = EvalPhase1(xMinus, allRxNodes, globalAbsorptionIndex, rxIndexMap, txIndexMap, nodeWeights);
                    grad[i] = (fPlus - fMinus) / (2 * h);
                }
                return grad;
            }
        );

        var lowerBound = Vector<double>.Build.Dense(totalParams);
        var upperBound = Vector<double>.Build.Dense(totalParams);

        lowerBound[globalAbsorptionIndex] = optimization.AbsorptionMin;
        upperBound[globalAbsorptionIndex] = optimization.AbsorptionMax;

        foreach (var rxId in uniqueRxIds)
        {
            lowerBound[rxIndexMap[rxId]] = optimization.RxAdjRssiMin;
            upperBound[rxIndexMap[rxId]] = optimization.RxAdjRssiMax;
        }
        foreach (var txId in uniqueTxIds)
        {
            lowerBound[txIndexMap[txId]] = optimization.TxRefRssiMin;
            upperBound[txIndexMap[txId]] = optimization.TxRefRssiMax;
        }

        var initialGuess = Vector<double>.Build.Dense(totalParams);

        var validAbsorptions = existingSettings.Values
            .Select(ns => ns.Calibration?.Absorption).Where(a => a.HasValue).Select(a => a!.Value).ToList();
        initialGuess[globalAbsorptionIndex] = Math.Clamp(
            validAbsorptions.Any() ? validAbsorptions.Average() : (optimization.AbsorptionMax + optimization.AbsorptionMin) / 2.0,
            optimization.AbsorptionMin, optimization.AbsorptionMax);

        foreach (var rxId in uniqueRxIds)
        {
            existingSettings.TryGetValue(rxId, out var ns);
            initialGuess[rxIndexMap[rxId]] = Math.Clamp(ns?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }
        foreach (var txId in uniqueTxIds)
        {
            existingSettings.TryGetValue(txId, out var ns);
            initialGuess[txIndexMap[txId]] = Math.Clamp(ns?.Calibration?.TxRefRssi ?? -59, optimization.TxRefRssiMin, optimization.TxRefRssiMax);
        }

        try
        {
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(objectiveFunction, lowerBound, upperBound, initialGuess);

            double globalAbsorptionValue = Math.Clamp(result.MinimizingPoint[globalAbsorptionIndex], optimization.AbsorptionMin, optimization.AbsorptionMax);

            foreach (var rxId in uniqueRxIds)
            {
                double rxAdjRssi = Math.Clamp(result.MinimizingPoint[rxIndexMap[rxId]], optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
                var n = or.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdjRssi;
                n.Absorption = globalAbsorptionValue;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            foreach (var txId in uniqueTxIds)
            {
                double txRefRssi = Math.Clamp(result.MinimizingPoint[txIndexMap[txId]], optimization.TxRefRssiMin, optimization.TxRefRssiMax);
                var n = or.Nodes.GetOrAdd(txId);
                n.TxRefRssi = txRefRssi;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            Log.Debug(Name + " Phase 1 completed with error: {0}, global absorption: {1}",
                result.FunctionInfoAtMinimum.Value, globalAbsorptionValue);
        }
        catch (Exception ex)
        {
            Log.Error("Error in Phase 1 optimization: {0}", ex.Message);
        }
    }

    private double EvalPhase1(Vector<double> x, List<Measure> allRxNodes, int globalAbsorptionIndex,
        Dictionary<string, int> rxIndexMap, Dictionary<string, int> txIndexMap,
        Dictionary<Measure, double> nodeWeights)
    {
        double squaredErrorSum = 0, weightSum = 0;
        double globalAbsorption = x[globalAbsorptionIndex];

        foreach (var node in allRxNodes)
        {
            if (node.Rx?.Location == null || node.Tx?.Location == null) continue;

            double rxAdjRssi = x[rxIndexMap[node.Rx.Id]];
            double txRefRssi = x[txIndexMap[node.Tx.Id]];
            double mapDistance = Math.Max(node.Rx.Location.DistanceTo(node.Tx.Location), 0.1);

            double predictedRssi = txRefRssi - 10 * globalAbsorption * Math.Log10(mapDistance);
            double diff = predictedRssi - node.GetAdjustedRssi(rxAdjRssi);

            double weight = nodeWeights[node];
            weightSum += weight;
            double error = diff < 0
                ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                : Math.Min(diff * diff, CappedError);
            squaredErrorSum += weight * error;
        }

        return weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum;
    }

    /// <summary>
    /// Phase 2: Antenna-only optimization. Holds absorption/rxAdj/txRef fixed from Phase 1 results
    /// and optimizes sinAz/cosAz/sinEl per directional node to minimize RSSI prediction error.
    /// Models asymmetric PCB antenna pattern: max(cos(θ), 0)² with backside null.
    /// </summary>
    private void Phase2_OptimizeAntennaAngles(OptimizationResults or, List<Measure> allRxNodes,
        List<string> directionalRxIds, Dictionary<Measure, double> nodeWeights,
        ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        // Build fixed parameter lookups from Phase 1 results
        var fixedRxAdj = new Dictionary<string, double>();
        var fixedTxRef = new Dictionary<string, double>();
        double fixedAbsorption = 3.0;
        foreach (var (id, pv) in or.Nodes)
        {
            if (pv.RxAdjRssi != null) fixedRxAdj[id] = pv.RxAdjRssi.Value;
            if (pv.TxRefRssi != null) fixedTxRef[id] = pv.TxRefRssi.Value;
            if (pv.Absorption != null) fixedAbsorption = pv.Absorption.Value;
        }

        // Build GMax lookup
        var rxGMaxMap = new Dictionary<string, double>();
        foreach (var rxId in directionalRxIds)
        {
            var rxNode = allRxNodes.First(m => m.Rx.Id == rxId).Rx;
            rxGMaxMap[rxId] = rxNode.GMax;
        }

        // Parameter layout: [sinAz_0, cosAz_0, sinEl_0, sinAz_1, cosAz_1, sinEl_1, ...]
        int N = directionalRxIds.Count;
        int totalParams = N * 3;
        var idxMap = new Dictionary<string, int>();
        for (int i = 0; i < N; i++)
            idxMap[directionalRxIds[i]] = i * 3; // base index; +0=sinAz, +1=cosAz, +2=sinEl

        double ComputeGainDb(double[] xa, Measure m)
        {
            if (!idxMap.TryGetValue(m.Rx.Id, out var baseIdx)) return 0.0;
            double sinAz = xa[baseIdx], cosAz = xa[baseIdx + 1], sinEl = xa[baseIdx + 2];
            double cosEl = Math.Sqrt(Math.Max(1.0 - sinEl * sinEl, 0.0));

            double bx = sinAz * cosEl, by = cosAz * cosEl, bz = sinEl;
            double dx = m.Tx.Location.X - m.Rx.Location.X;
            double dy = m.Tx.Location.Y - m.Rx.Location.Y;
            double dz = m.Tx.Location.Z - m.Rx.Location.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9) return 10.0 * Math.Log10(rxGMaxMap[m.Rx.Id]);

            double cosTheta = (bx * dx + by * dy + bz * dz) / len;
            double cosClamped = Math.Max(cosTheta, 0.0); // Asymmetric: backside null
            double cos2 = Math.Max(cosClamped * cosClamped, 1e-3);
            return 10.0 * Math.Log10(rxGMaxMap[m.Rx.Id] * cos2);
        }

        double EvalPhase2(double[] xa)
        {
            double squaredErrorSum = 0, weightSum = 0;
            double penalty = 0;

            // Unit-circle regularization
            for (int i = 0; i < N; i++)
            {
                double sa = xa[i * 3], ca = xa[i * 3 + 1];
                double dev = sa * sa + ca * ca - 1.0;
                penalty += 0.1 * dev * dev;
            }

            foreach (var node in allRxNodes)
            {
                if (node.Rx?.Location == null || node.Tx?.Location == null) continue;

                double rxAdj = fixedRxAdj.GetValueOrDefault(node.Rx.Id, 0);
                double txRef = fixedTxRef.GetValueOrDefault(node.Tx.Id, -59);
                double mapDistance = Math.Max(node.Rx.Location.DistanceTo(node.Tx.Location), 0.1);

                double gainDb = ComputeGainDb(xa, node);
                double predictedRssi = txRef + gainDb - 10 * fixedAbsorption * Math.Log10(mapDistance);
                double diff = predictedRssi - node.GetAdjustedRssi(rxAdj);

                double weight = nodeWeights[node];
                weightSum += weight;
                double error = diff < 0
                    ? Math.Min(AsymmetricErrorFactor * diff * diff, CappedError)
                    : Math.Min(diff * diff, CappedError);
                squaredErrorSum += weight * error;
            }

            return (weightSum > 0 ? squaredErrorSum / weightSum : squaredErrorSum) + penalty;
        }

        var obj = ObjectiveFunction.Gradient(
            x => EvalPhase2(x.ToArray()),
            x =>
            {
                var xa = x.ToArray();
                var grad = Vector<double>.Build.Dense(totalParams);
                double h = 1e-5;
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

        // Bounds and initial guess
        var lower = new double[totalParams];
        var upper = new double[totalParams];
        var init = new double[totalParams];
        for (int i = 0; i < N; i++)
        {
            existingSettings.TryGetValue(directionalRxIds[i], out var ns);
            double azDeg = ns?.Calibration?.Azimuth ?? 0.0;
            double elDeg = ns?.Calibration?.Elevation ?? 0.0;
            double azRad = azDeg * Math.PI / 180.0;
            double elRad = elDeg * Math.PI / 180.0;

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

                var n = or.Nodes.GetOrAdd(directionalRxIds[i]);
                n.Azimuth = azDeg;
                n.Elevation = elRad * 180.0 / Math.PI;
            }

            Log.Debug(Name + " Phase 2 (antenna) completed with error: {0}", result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            Log.Error("Error in Phase 2 antenna optimization: {0}", ex.Message);
        }
    }
}
