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

        var objectiveFunction = ObjectiveFunction.Gradient(
            x =>
            {
                double squaredErrorSum = 0;
                double weightSum = 0;

                // Get the global absorption value
                double globalAbsorption = x[globalAbsorptionIndex];

                foreach (var node in allRxNodes)
                {
                    if (node.Rx?.Location == null || node.Tx?.Location == null)
                    {
                        continue; // Skip measurements with missing locations
                    }

                    int rxIndex = rxIndexMap[node.Rx.Id];
                    int txIndex = txIndexMap[node.Tx.Id];

                    double rxAdjRssi = x[rxIndex];
                    double txRefRssi = x[txIndex];

                    double mapDistance = node.Rx.Location.DistanceTo(node.Tx.Location);
                    if (mapDistance <= 0)
                    {
                        mapDistance = 0.1; // Prevent log(0) error
                    }

                    // Calculate the predicted RSSI using the path loss model
                    double predictedRssi = txRefRssi - 10 * globalAbsorption * Math.Log10(mapDistance);
                    double adjustedMeasuredRssi = node.GetAdjustedRssi(rxAdjRssi);
                    // Compare with the adjusted measured RSSI
                    double diff = predictedRssi - adjustedMeasuredRssi;

                    // Get the weight for this node
                    double weight = nodeWeights[node];
                    weightSum += weight;

                    // Calculate error with asymmetric penalty
                    double error;
                    if (diff < 0)
                    {
                        // Predicted RSSI is less than the adjusted measured RSSI
                        error = Math.Min(AsymmetricErrorFactor * Math.Pow(diff, 2), CappedError);
                    }
                    else
                    {
                        error = Math.Min(Math.Pow(diff, 2), CappedError);
                    }

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
                    var xPlus = x.Clone();
                    var xMinus = x.Clone();
                    xPlus[i] = x[i] + h;
                    xMinus[i] = x[i] - h;

                    double fPlus = 0;
                    double fMinus = 0;
                    double weightSumPlus = 0;
                    double weightSumMinus = 0;

                    foreach (var measure in allRxNodes)
                    {
                        if (measure.Rx?.Location == null || measure.Tx?.Location == null)
                        {
                            continue;
                        }

                        int rxIndex = rxIndexMap[measure.Rx.Id];
                        int txIndex = txIndexMap[measure.Tx.Id];
                        double weight = nodeWeights[measure];
                        double mapDistance = measure.Rx.Location.DistanceTo(measure.Tx.Location);
                        if (mapDistance <= 0)
                        {
                            mapDistance = 0.1;
                        }

                        // Plus calculation
                        {
                            double globalAbsorption = xPlus[globalAbsorptionIndex];
                            double rxAdjRssi = xPlus[rxIndex];
                            double txRefRssi = xPlus[txIndex];

                            double predictedRssi = txRefRssi - 10 * globalAbsorption * Math.Log10(mapDistance);
                            double diff = predictedRssi - measure.GetAdjustedRssi(rxAdjRssi);

                            double error;
                            if (diff < 0)
                            {
                                error = Math.Min(AsymmetricErrorFactor * Math.Pow(diff, 2), CappedError);
                            }
                            else
                            {
                                error = Math.Min(Math.Pow(diff, 2), CappedError);
                            }

                            fPlus += weight * error;
                            weightSumPlus += weight;
                        }

                        // Minus calculation
                        {
                            double globalAbsorption = xMinus[globalAbsorptionIndex];
                            double rxAdjRssi = xMinus[rxIndex];
                            double txRefRssi = xMinus[txIndex];

                            double predictedRssi = txRefRssi - 10 * globalAbsorption * Math.Log10(mapDistance);
                            double diff = predictedRssi - measure.GetAdjustedRssi(rxAdjRssi);

                            double error;
                            if (diff < 0)
                            {
                                error = Math.Min(AsymmetricErrorFactor * Math.Pow(diff, 2), CappedError);
                            }
                            else
                            {
                                error = Math.Min(Math.Pow(diff, 2), CappedError);
                            }

                            fMinus += weight * error;
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

        // Calculate the average absorption from existing settings for initial guess
        var validAbsorptions = existingSettings.Values
            .Select(ns => ns.Calibration?.Absorption)
            .Where(a => a.HasValue)
            .Select(a => a!.Value)
            .ToList();

        double initialGlobalAbsorptionGuess;
        if (validAbsorptions.Any())
        {
            initialGlobalAbsorptionGuess = validAbsorptions.Average();
        }
        else
        {
            // Fallback to midpoint if no nodes have absorption set
            initialGlobalAbsorptionGuess = (optimization.AbsorptionMax + optimization.AbsorptionMin) / 2.0;
        }
        // Clamp the initial guess for global absorption
        initialGuess[globalAbsorptionIndex] = Math.Clamp(initialGlobalAbsorptionGuess, optimization.AbsorptionMin, optimization.AbsorptionMax);

        // Initialize Rx node parameters
        foreach (var rxId in uniqueRxIds)
        {
            existingSettings.TryGetValue(rxId, out var nodeSettings);
            // Clamp initial guess within global bounds
            initialGuess[rxIndexMap[rxId]] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }

        // Initialize Tx node parameters
        foreach (var txId in uniqueTxIds)
        {
            existingSettings.TryGetValue(txId, out var nodeSettings);
            // Clamp initial guess within global bounds
            initialGuess[txIndexMap[txId]] = Math.Clamp(nodeSettings?.Calibration?.TxRefRssi ?? -59, optimization.TxRefRssiMin, optimization.TxRefRssiMax);
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
            Log.Debug(Name + " completed with error: {0}, global absorption: {1}",
                result.FunctionInfoAtMinimum.Value, globalAbsorptionValue);
        }
        catch (Exception ex)
        {
            Log.Error("Error optimizing: {0}", ex.Message);
        }

        return or;
    }
}
