using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using ESPresense.Extensions;

namespace ESPresense.Optimizers;

public class PerNodeAbsorptionRxTx : IOptimizer
{
    private readonly State _state;

    public PerNodeAbsorptionRxTx(State state)
    {
        _state = state;
    }

    public string Name => "Per Node Absorption Rx Tx Adj";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var or = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        // Group all valid measurements
        var allRxNodes = os.ByRx().SelectMany(g => g).ToList();

        if (allRxNodes.Count < 3)
        {
            Log.Warning("Not enough valid measurements for optimization");
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
        double penaltyWeight = 10;

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

        // Define asymmetric error function that penalizes impossible situations
        // (when estimated distance is less than actual map distance)
        Func<double, double, double> calculateDistanceError = (calculated, map) =>
        {
            if (calculated < map)
            {
                // This is physically impossible (can't be closer than map distance)
                // Apply higher penalty with asymmetric factor
                return Math.Pow(map - calculated, 4);
            }
            else
            {
                // Regular error calculation for the normal case (estimated >= actual)
                // This means there could be an obstacle causing signal attenuation
                return Math.Pow(map - calculated, 2);
            }
        };

        var objectiveFunction = ObjectiveFunction.Gradient(
            x =>
            {
                double error = 0;
                double weightSum = 0;

                foreach (var node in allRxNodes)
                {
                    int rxBaseIndex = rxIndexMap[node.Rx.Id];
                    int txBaseIndex = txIndexMap[node.Tx.Id];

                    double rxAdjRssi = x[rxBaseIndex];
                    double absorption = x[rxBaseIndex + 1];
                    double txRefRssi = x[txBaseIndex];

                    double calculatedDistance = Math.Pow(10, (txRefRssi - node.GetAdjustedRssi(rxAdjRssi)) / (10.0 * absorption));
                    double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                    // Get the weight for this node
                    double weight = nodeWeights[node];
                    weightSum += weight;

                    // Apply weight to the asymmetric error
                    error += weight * calculateDistanceError(calculatedDistance, mapDistance);

                    // Regularization: Penalize absorption deviation from the middle value
                    error += weight * penaltyWeight * Math.Pow(absorption - targetAbsorption, 2);
                }

                return weightSum > 0 ? error / weightSum : error;
            },
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

                    double fPlus = 0;
                    double fMinus = 0;
                    double weightSumPlus = 0;
                    double weightSumMinus = 0;

                    foreach (var node in allRxNodes)
                    {
                        int rxBaseIndex = rxIndexMap[node.Rx.Id];
                        int txBaseIndex = txIndexMap[node.Tx.Id];
                        double weight = nodeWeights[node];

                        {
                            double rxAdjRssi = xPlus[rxBaseIndex];
                            double absorption = xPlus[rxBaseIndex + 1];
                            double txRefRssi = xPlus[txBaseIndex];
                            double calculatedDistance = Math.Pow(10, (txRefRssi - node.GetAdjustedRssi(rxAdjRssi)) / (10.0 * absorption));

                            double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                            fPlus += weight * (calculateDistanceError(calculatedDistance, mapDistance)
                                     + penaltyWeight * Math.Pow(absorption - targetAbsorption, 2));
                            weightSumPlus += weight;
                        }
                        {
                            double rxAdjRssi = xMinus[rxBaseIndex];
                            double absorption = xMinus[rxBaseIndex + 1];
                            double txRefRssi = xMinus[txBaseIndex];
                            double calculatedDistance = Math.Pow(10, (txRefRssi - node.GetAdjustedRssi(rxAdjRssi)) / (10.0 * absorption));

                            double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                            fMinus += weight * (calculateDistanceError(calculatedDistance, mapDistance)
                                      + penaltyWeight * Math.Pow(absorption - targetAbsorption, 2));
                            weightSumMinus += weight;
                        }
                    }

                    fPlus = weightSumPlus > 0 ? fPlus / weightSumPlus : fPlus;
                    fMinus = weightSumMinus > 0 ? fMinus / weightSumMinus : fMinus;
                    grad[i] = (fPlus - fMinus) / (2 * h);
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
            existingSettings.TryGetValue(rxId, out var nodeSettings);
            // Clamp initial guess within global bounds
            initialGuess[baseIndex] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
            // Clamp initial guess within global bounds
            initialGuess[baseIndex + 1] = Math.Clamp(nodeSettings?.Calibration?.Absorption ?? ((optimization.AbsorptionMax - optimization.AbsorptionMin) / 2.0) + optimization.AbsorptionMin, optimization.AbsorptionMin, optimization.AbsorptionMax);
        }
        foreach (var txId in uniqueTxIds)
        {
            existingSettings.TryGetValue(txId, out var nodeSettings);
            // Initial guess uses node setting if available, else -59
            // Clamp initial guess within global bounds
            initialGuess[txIndexMap[txId]] = Math.Clamp(nodeSettings?.Calibration?.TxRefRssi ?? -59, optimization.TxRefRssiMin, optimization.TxRefRssiMax);
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