using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class PerNodeAbsorptionRxTx : IOptimizer
{
    private readonly Func<ConfigOptimization?> _getOptimization;

    public PerNodeAbsorptionRxTx(State state)
    {
        _getOptimization = () => state.Config?.Optimization;
    }

    internal PerNodeAbsorptionRxTx(ConfigOptimization optimization) => _getOptimization = () => optimization;

    public string Name => "Per Node Absorption Rx Tx Adj";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _getOptimization();

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

        // Map parameter indices
        var rxIndexMap = new Dictionary<string, int>();
        var txIndexMap = new Dictionary<string, int>();
        int paramIndex = 0;

        // Each Rx node has two parameters: rxAdjRssi and absorption
        foreach (var rxId in uniqueRxIds)
        {
            rxIndexMap[rxId] = paramIndex;
            paramIndex += 2;
        }

        // Each Tx node has one parameter: txRefRssi
        foreach (var txId in uniqueTxIds)
        {
            txIndexMap[txId] = paramIndex;
            paramIndex++;
        }

        int totalParams = paramIndex;

        if (optimization == null) return or;

        var targetAbsorption = optimization.AbsorptionMin + (optimization.AbsorptionMax - optimization.AbsorptionMin) / 2.0;
        var huberDelta = optimization.EffectiveHuberDelta;
        const double absorptionRegularization = 0.1;
        const double rssiRegularization = 0.01;

        // Pre-calculate weights for each node based on RssiVar
        var nodeWeights = new Dictionary<Measure, double>();
        double totalWeight = 0;

        foreach (var node in allRxNodes)
        {
            // Inverse variance weighting - use 1/variance as weight
            double weight = 1.0;
            if (node.RssiVar > 0)
            {
                weight = 1.0 / Math.Max(node.RssiVar.Value, 0.1); // Adding minimum to avoid extreme weights
            }

            nodeWeights[node] = weight;
            totalWeight += weight;
        }

        // Normalize weights if needed
        if (totalWeight > 0)
        {
            foreach (var node in allRxNodes)
            {
                nodeWeights[node] = nodeWeights[node] / totalWeight * allRxNodes.Count;
            }
        }

        var rxPriors = uniqueRxIds.ToDictionary(
            id => id,
            id => Math.Clamp(
                existingSettings.TryGetValue(id, out var settings) ? settings.Calibration.RxAdjRssi ?? 0 : 0,
                optimization.RxAdjRssiMin,
                optimization.RxAdjRssiMax));
        var txPriors = uniqueTxIds.ToDictionary(
            id => id,
            id => Math.Clamp(
                existingSettings.TryGetValue(id, out var settings) && settings.Calibration.TxRefRssi is { } configured
                    ? configured
                    : allRxNodes.Where(measure => measure.Tx.Id == id && double.IsFinite(measure.RefRssi) && measure.RefRssi != 0)
                        .Select(measure => measure.RefRssi)
                        .DefaultIfEmpty(-59)
                        .Average(),
                optimization.TxRefRssiMin,
                optimization.TxRefRssiMax));

        double CalculateObjective(Vector<double> x)
        {
            double loss = 0;
            double weightSum = 0;

            foreach (var node in allRxNodes)
            {
                int rxBaseIndex = rxIndexMap[node.Rx.Id];
                int txBaseIndex = txIndexMap[node.Tx.Id];
                double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);
                if (!double.IsFinite(mapDistance) || mapDistance <= 0) continue;

                double rxAdjRssi = x[rxBaseIndex];
                double absorption = x[rxBaseIndex + 1];
                double txRefRssi = x[txBaseIndex];
                double predictedRssi = txRefRssi - 10 * absorption * Math.Log10(mapDistance);
                double measuredRssi = node.GetAdjustedRssi(rxAdjRssi);
                if (!double.IsFinite(predictedRssi) || !double.IsFinite(measuredRssi)) continue;

                double residual = Math.Abs(predictedRssi - measuredRssi);
                double huberLoss = residual <= huberDelta
                    ? 0.5 * residual * residual
                    : huberDelta * (residual - 0.5 * huberDelta);
                double weight = nodeWeights[node];
                loss += weight * huberLoss;
                weightSum += weight;
            }

            if (weightSum <= 0) return double.PositiveInfinity;

            double absorptionPenalty = uniqueRxIds
                .Average(id => Math.Pow(x[rxIndexMap[id] + 1] - targetAbsorption, 2));
            double rxPenalty = uniqueRxIds
                .Average(id => Math.Pow(x[rxIndexMap[id]] - rxPriors[id], 2));
            double txPenalty = uniqueTxIds
                .Average(id => Math.Pow(x[txIndexMap[id]] - txPriors[id], 2));

            return loss / weightSum
                   + absorptionRegularization * absorptionPenalty
                   + rssiRegularization * (rxPenalty + txPenalty);
        }

        var objectiveFunction = ObjectiveFunction.Gradient(
            CalculateObjective,
            x =>
            {
                var grad = Vector<double>.Build.Dense(totalParams);
                double h = 1e-6;
                for (int i = 0; i < totalParams; i++)
                {
                    var xPlus = x.Clone();
                    var xMinus = x.Clone();
                    xPlus[i] = x[i] + h;
                    xMinus[i] = x[i] - h;
                    grad[i] = (CalculateObjective(xPlus) - CalculateObjective(xMinus)) / (2 * h);
                }
                return grad;
            }
        );


        // Build lower and upper bound vectors
        var lowerBound = Vector<double>.Build.Dense(totalParams);
        var upperBound = Vector<double>.Build.Dense(totalParams);

        // For Rx nodes: rxAdjRssi and absorption bounds
        foreach (var rxId in uniqueRxIds)
        {
            int baseIndex = rxIndexMap[rxId];
            lowerBound[baseIndex] = optimization.RxAdjRssiMin;
            upperBound[baseIndex] = optimization.RxAdjRssiMax;
            lowerBound[baseIndex + 1] = optimization.AbsorptionMin;
            upperBound[baseIndex + 1] = optimization.AbsorptionMax;
        }

        // For Tx nodes: txRefRssi bounds
        foreach (var txId in uniqueTxIds)
        {
            lowerBound[txIndexMap[txId]] = optimization.TxRefRssiMin;
            upperBound[txIndexMap[txId]] = optimization.TxRefRssiMax;
        }

        // Initialize with a reasonable guess (ensure within bounds)
        var initialGuess = Vector<double>.Build.Dense(totalParams);
        foreach (var rxId in uniqueRxIds)
        {
            int baseIndex = rxIndexMap[rxId];
            initialGuess[baseIndex] = rxPriors[rxId];
            // Do not seed from a previously railed value; that can trap the bounded solver at the same edge.
            initialGuess[baseIndex + 1] = targetAbsorption;
        }
        foreach (var txId in uniqueTxIds)
        {
            initialGuess[txIndexMap[txId]] = txPriors[txId];
        }

        try
        {
            // Use the bounded BFGS solver
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(objectiveFunction, lowerBound, upperBound, initialGuess);

            // Process Rx node results
            foreach (var rxId in uniqueRxIds)
            {
                int baseIndex = rxIndexMap[rxId];
                double rxAdjRssi = result.MinimizingPoint[baseIndex];
                double absorption = result.MinimizingPoint[baseIndex + 1];

                // Ensure values are within bounds (should be already)
                rxAdjRssi = Math.Max(optimization.RxAdjRssiMin, Math.Min(rxAdjRssi, optimization.RxAdjRssiMax));
                absorption = Math.Max(optimization.AbsorptionMin, Math.Min(absorption, optimization.AbsorptionMax));

                var n = or.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdjRssi;
                n.Absorption = absorption;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }

            // Process Tx node results
            foreach (var txId in uniqueTxIds)
            {
                double txRefRssi = result.MinimizingPoint[txIndexMap[txId]];
                txRefRssi = Math.Max(optimization.TxRefRssiMin, Math.Min(txRefRssi, optimization.TxRefRssiMax));

                var n = or.Nodes.GetOrAdd(txId);
                n.TxRefRssi = txRefRssi;
                n.Error = result.FunctionInfoAtMinimum.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing all nodes: {0}", ex.Message);
        }

        return or;
    }
}
