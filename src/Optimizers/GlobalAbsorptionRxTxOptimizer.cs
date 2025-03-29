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
    // Higher value means we penalize "impossible" cases more strongly (estimated < actual)
    private const double AsymmetricErrorFactor = 5;

    public GlobalAbsorptionRxTxOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Global Absorption Rx Tx";

    public OptimizationResults Optimize(OptimizationSnapshot os)
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

        // Global absorption parameter is the first parameter
        int globalAbsorptionIndex = paramIndex++;

        // Each Rx node has one parameter: rxAdjRssi
        foreach (var rxId in uniqueRxIds)
        {
            rxIndexMap[rxId] = paramIndex++;
        }

        // Each Tx node has one parameter: txRefRssi
        foreach (var txId in uniqueTxIds)
        {
            txIndexMap[txId] = paramIndex++;
        }

        int totalParams = paramIndex;

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
        Func<double, double, double> calculateDistanceError = (estimated, actual) =>
        {
            if (estimated < actual)
            {
                // This is physically impossible (can't be closer than map distance)
                // Apply higher penalty with asymmetric factor
                return Math.Pow(actual - estimated, 2) * (1.0 + AsymmetricErrorFactor);
            }
            else
            {
                // Regular error calculation for the normal case (estimated >= actual)
                // This means there could be an obstacle causing signal attenuation
                return Math.Pow(actual - estimated, 2);
            }
        };

        var objectiveFunction = ObjectiveFunction.Gradient(
            x =>
            {
                double error = 0;
                double weightSum = 0;

                // Get the global absorption value
                double globalAbsorption = x[globalAbsorptionIndex];

                foreach (var node in allRxNodes)
                {
                    int rxIndex = rxIndexMap[node.Rx.Id];
                    int txIndex = txIndexMap[node.Tx.Id];

                    double rxAdjRssi = x[rxIndex];
                    double txRefRssi = x[txIndex];

                    double expectedDistance = Math.Pow(10, (txRefRssi + rxAdjRssi - node.Rssi) / (10.0 * globalAbsorption));
                    double actualDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                    // Get the weight for this node
                    double weight = nodeWeights[node];
                    weightSum += weight;

                    // Apply weight to the asymmetric error
                    error += weight * calculateDistanceError(expectedDistance, actualDistance);
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
                        int rxIndex = rxIndexMap[node.Rx.Id];
                        int txIndex = txIndexMap[node.Tx.Id];
                        double weight = nodeWeights[node];

                        {
                            double globalAbsorption = xPlus[globalAbsorptionIndex];
                            double rxAdjRssi = xPlus[rxIndex];
                            double txRefRssi = xPlus[txIndex];
                            double expectedDistance = Math.Pow(10, (txRefRssi + rxAdjRssi - node.Rssi) / (10.0 * globalAbsorption));
                            double actualDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                            fPlus += weight * calculateDistanceError(expectedDistance, actualDistance);
                            weightSumPlus += weight;
                        }
                        {
                            double globalAbsorption = xMinus[globalAbsorptionIndex];
                            double rxAdjRssi = xMinus[rxIndex];
                            double txRefRssi = xMinus[txIndex];
                            double expectedDistance = Math.Pow(10, (txRefRssi + rxAdjRssi - node.Rssi) / (10.0 * globalAbsorption));
                            double actualDistance = node.Rx.Location.DistanceTo(node.Tx.Location);

                            fMinus += weight * calculateDistanceError(expectedDistance, actualDistance);
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

        // Set bounds for global absorption
        lowerBound[globalAbsorptionIndex] = optimization.AbsorptionMin;
        upperBound[globalAbsorptionIndex] = optimization.AbsorptionMax;

        // For Rx nodes: rxAdjRssi bounds
        foreach (var rxId in uniqueRxIds)
        {
            lowerBound[rxIndexMap[rxId]] = optimization.RxAdjRssiMin;
            upperBound[rxIndexMap[rxId]] = optimization.RxAdjRssiMax;
        }

        // For Tx nodes: txRefRssi bounds
        foreach (var txId in uniqueTxIds)
        {
            lowerBound[txIndexMap[txId]] = optimization.TxRefRssiMin;
            upperBound[txIndexMap[txId]] = optimization.TxRefRssiMax;
        }

        // Initialize with a reasonable guess (ensure within bounds)
        var initialGuess = Vector<double>.Build.Dense(totalParams);

        // Initialize global absorption to middle of range
        initialGuess[globalAbsorptionIndex] = (optimization.AbsorptionMax + optimization.AbsorptionMin) / 2.0;

        // Initialize Rx node parameters
        foreach (var rxId in uniqueRxIds)
        {
            initialGuess[rxIndexMap[rxId]] = 0; // initial rxAdjRssi
        }

        // Initialize Tx node parameters
        foreach (var txId in uniqueTxIds)
        {
            initialGuess[txIndexMap[txId]] = -59; // initial txRefRssi (typical value)
        }

        try
        {
            // Use the bounded BFGS solver
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(objectiveFunction, lowerBound, upperBound, initialGuess);

            // Get the optimized global absorption value
            double globalAbsorptionValue = result.MinimizingPoint[globalAbsorptionIndex];
            globalAbsorptionValue = Math.Max(optimization.AbsorptionMin, Math.Min(globalAbsorptionValue, optimization.AbsorptionMax));


            // Process Rx node results
            foreach (var rxId in uniqueRxIds)
            {
                double rxAdjRssi = result.MinimizingPoint[rxIndexMap[rxId]];
                rxAdjRssi = Math.Max(optimization.RxAdjRssiMin, Math.Min(rxAdjRssi, optimization.RxAdjRssiMax));

                var n = or.Nodes.GetOrAdd(rxId);
                n.RxAdjRssi = rxAdjRssi;
                n.Absorption = globalAbsorptionValue;
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

            // Log the optimization results
            Log.Information("Optimization completed with error: {0}, global absorption: {1}",
                result.FunctionInfoAtMinimum.Value, globalAbsorptionValue);
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing: {0}", ex.Message);
        }

        return or;
    }
}
