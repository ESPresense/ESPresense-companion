using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class CombinedOptimizer : IOptimizer
{
    private readonly State _state;
    private readonly Serilog.ILogger _logger;

    public CombinedOptimizer(State state)
    {
        _state = state;
        _logger = Log.ForContext<CombinedOptimizer>();
    }

    public string Name => "Two-Step Optimized Combined RxAdjRssi and Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        var allNodes = os.ByRx().SelectMany(g => g).ToList();
        var uniqueDeviceIds = allNodes.SelectMany(n => new[] { n.Rx.Id, n.Tx.Id }).Distinct().ToList();

        if (allNodes.Count < 3)
        {
            _logger.Information("Not enough nodes for optimization (need at least 3, found {Count})", allNodes.Count);
            return results;
        }

        try
        {
            // Step 1: Optimize RxAdjRssi and path-specific absorptions
            var (rxAdjRssiDict, pathAbsorptionDict, error) = OptimizeRxAdjRssiAndPathAbsorption(allNodes, uniqueDeviceIds, optimization, existingSettings);

            // Step 2: Optimize node-specific absorptions while keeping RxAdjRssi constant
            var nodeAbsorptions = OptimizeNodeAbsorptions(allNodes, uniqueDeviceIds, rxAdjRssiDict, pathAbsorptionDict, optimization, existingSettings);

            // Process and store results
            foreach (var deviceId in uniqueDeviceIds)
            {
                if (deviceParams.TryGetValue(deviceId, out var parameters))
                {
                    results.Nodes[deviceId] = new ProposedValues
                    {
                        RxAdjRssi = parameters.RxAdjRssi,
                        Absorption = parameters.Absorption,
                        Error = error
                    };
                }
            }

            _logger.Information("Optimization completed with error: {Error}", error);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in combined optimization");
        }

        return results;
    }

    private (Dictionary<string, double> RxAdjRssi, Dictionary<(string, string), double> PathAbsorption, double Error)
        OptimizeRxAdjRssiAndPathAbsorption(List<Measure> allNodes, List<string> uniqueDeviceIds, ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        var pathPairs = new HashSet<(string, string)>(allNodes.Select(n => (Min(n.Rx.Id, n.Tx.Id), Max(n.Rx.Id, n.Tx.Id))));

        var obj = ObjectiveFunction.Value(x =>
        {
            var rxAdjRssiDict = new Dictionary<string, double>();
            var absorptionDict = new Dictionary<(string, string), double>();

            for (int i = 0; i < uniqueDeviceIds.Count; i++)
            {
                var rxAdjRssi = x[i];
                existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
                // Bounds should always come from global config
                double rxAdjMin = optimization?.RxAdjRssiMin ?? -15;
                double rxAdjMax = optimization?.RxAdjRssiMax ?? 25;
                if (rxAdjRssi < rxAdjMin || rxAdjRssi > rxAdjMax)
                    return double.PositiveInfinity;
                rxAdjRssiDict[uniqueDeviceIds[i]] = rxAdjRssi;
            }

            int offset = uniqueDeviceIds.Count;
            foreach (var pair in pathPairs)
            {
                var absorption = x[offset++];
                if (absorption <= optimization?.AbsorptionMin || absorption >= optimization?.AbsorptionMax)
                    return double.PositiveInfinity;
                absorptionDict[pair] = absorption;
            }

            return CalculateError(allNodes, rxAdjRssiDict, pathAbsorptionDict: absorptionDict);
        });

        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count + pathPairs.Count);
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
            // Initial guess uses node setting if available, else 0
            // Clamp initial guess within global bounds
            initialGuess[i] = Math.Clamp(nodeSettings?.Calibration?.RxAdjRssi ?? 0, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
        }
        for (int i = uniqueDeviceIds.Count; i < initialGuess.Count; i++)
        {
            // Clamp initial guess within global bounds (Path absorption uses global midpoint, clamped)
            initialGuess[i] = Math.Clamp((optimization?.AbsorptionMax - optimization?.AbsorptionMin) / 2 + optimization?.AbsorptionMin ?? 3d, optimization.AbsorptionMin, optimization.AbsorptionMax);
        }

        var solver = new NelderMeadSimplex(1e-7, 20000);
        var result = solver.FindMinimum(obj, initialGuess);

        var rxAdjRssiDict = new Dictionary<string, double>();
        var pathAbsorptionDict = new Dictionary<(string, string), double>();

        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            rxAdjRssiDict[uniqueDeviceIds[i]] = result.MinimizingPoint[i];
        }

        int pathOffset = uniqueDeviceIds.Count;
        foreach (var pair in pathPairs)
        {
            pathAbsorptionDict[pair] = result.MinimizingPoint[pathOffset++];
        }

        return (rxAdjRssiDict, pathAbsorptionDict, result.FunctionInfoAtMinimum.Value);
    }

    private Dictionary<string, double> OptimizeNodeAbsorptions(List<Measure> allNodes, List<string> uniqueDeviceIds,
        Dictionary<string, double> rxAdjRssiDict, Dictionary<(string, string), double> pathAbsorptionDict, ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        // Create reasonable initial guesses
        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count * 2);
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            // Include more intelligent initial guesses based on naive distance model
            // Attempt to calculate a reasonable starting point based on physics model
            double estimatedRxAdjRssi = 0;
            double estimatedAbsorption = 2.5; // Middle of typical range (between 2-3)

            // If we have data from existing nodes, try to extract better initial guesses
            var existingMeasurements = allNodes.Where(n =>
                n.Rx.Id == uniqueDeviceIds[i] || n.Tx.Id == uniqueDeviceIds[i]).ToList();

            if (existingMeasurements.Any())
            {
                // Estimate parameters based on known distances and RSSI
                // This is a simplified approach, but provides a better starting point
                var avgDistance = existingMeasurements.Average(m => m.Rx.Location.DistanceTo(m.Tx.Location));
                var avgRssi = existingMeasurements.Average(m => m.Rssi);

                // Heuristic formula based on RSSI model
                if (avgDistance > 0 && !double.IsNaN(avgRssi))
                {
                    var absorption = x[i];
                    existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
                    // Bounds should always come from global config
                    double absorptionMin = optimization?.AbsorptionMin ?? 2.5;
                    double absorptionMax = optimization?.AbsorptionMax ?? 3.5;
                    if (absorption < absorptionMin || absorption > absorptionMax)
                        return double.PositiveInfinity;
                    nodeAbsorptionDict[uniqueDeviceIds[i]] = absorption;
                }

                return CalculateError(allNodes, rxAdjRssiDict, nodeAbsorptionDict: nodeAbsorptionDict);
            },
            // Function to compute gradient
            x => {
                try
                {
                    nodeAbsorptionDict[uniqueDeviceIds[i]] = x[i];
                }

                // Numerically approximate the gradient
                var gradient = Vector<double>.Build.Dense(x.Count);
                double epsilon = 1e-5;
                double baseError = CalculateError(allNodes, rxAdjRssiDict, nodeAbsorptionDict: nodeAbsorptionDict);

                    // Compute gradient numerically
                    var gradient = Vector<double>.Build.Dense(x.Count);
                    double h = 1e-5; // Step size for finite difference

                    for (int i = 0; i < x.Count; i++)
                    {
                        var xPlus = x.Clone();
                        xPlus[i] += h;

                        var paramsPlus = CreateDeviceParamsFromVector(xPlus, uniqueDeviceIds, optimization);
                        var errorPlus = CalculateError(allNodes, paramsPlus);

                        gradient[i] = (errorPlus - baseError) / h;
                    }

                    return gradient;
                }
                catch (Exception ex)
                {
                    var tempDict = new Dictionary<string, double>(nodeAbsorptionDict);
                    tempDict[uniqueDeviceIds[i]] += epsilon;

                    var errorPlusEps = CalculateError(allNodes, rxAdjRssiDict, nodeAbsorptionDict: tempDict);
                    gradient[i] = (errorPlusEps - baseError) / epsilon;
                }
            }
        );

        // ConjugateGradientMinimizer only takes 3 tolerance parameters, not a maximum iteration count
        var solver = new ConjugateGradientMinimizer(1e-3, 1000);

        // Initial guess uses node setting if available, else global midpoint
        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count);
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
            double absorptionMin = optimization?.AbsorptionMin ?? 2.5; // Need global bounds for fallback midpoint
            double absorptionMax = optimization?.AbsorptionMax ?? 3.5;
            // Clamp initial guess within global bounds
            initialGuess[i] = Math.Clamp(nodeSettings?.Calibration?.Absorption ?? (absorptionMax - absorptionMin) / 2 + absorptionMin, absorptionMin, absorptionMax);
        }

        var solver = new BfgsMinimizer(1e-7, 1e-7, 1e-7);
        var result = solver.FindMinimum(obj, initialGuess);

        var nodeAbsorptions = new Dictionary<string, double>();
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            result = solver.FindMinimum(objGradient, initialGuess);
            _logger.Information("Optimization completed: Iterations={0}, Status={1}, Error={2}",
                result.Iterations, result.ReasonForExit, result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Optimization failed");

            // Return default values if optimization fails
            var defaultParams = new Dictionary<string, DeviceParameters>();
            foreach (var id in uniqueDeviceIds)
            {
                defaultParams[id] = new DeviceParameters
                {
                    RxAdjRssi = 0,
                    Absorption = (optimization?.AbsorptionMax + optimization?.AbsorptionMin) / 2 ?? 3.0
                };
            }

            return (defaultParams, double.MaxValue);
        }

        // Extract optimized parameters
        var deviceParams = new Dictionary<string, DeviceParameters>();
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            deviceParams[uniqueDeviceIds[i]] = new DeviceParameters
            {
                RxAdjRssi = result.MinimizingPoint[i],
                Absorption = result.MinimizingPoint[i + uniqueDeviceIds.Count]
            };
        }

        return (deviceParams, result.FunctionInfoAtMinimum.Value);
    }

    private Dictionary<string, DeviceParameters> CreateDeviceParamsFromVector(Vector<double> x, List<string> uniqueDeviceIds, ConfigOptimization optimization)
    {
        var deviceParams = new Dictionary<string, DeviceParameters>();

        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            var rxAdjRssi = x[i];
            var absorption = x[i + uniqueDeviceIds.Count];

            // Enforce constraints by clamping values to valid ranges
            rxAdjRssi = Math.Clamp(rxAdjRssi,
                optimization?.RxAdjRssiMin ?? -20,
                optimization?.RxAdjRssiMax ?? 20);

            absorption = Math.Clamp(absorption,
                optimization?.AbsorptionMin ?? 1.5,
                optimization?.AbsorptionMax ?? 4.5);

            deviceParams[uniqueDeviceIds[i]] = new DeviceParameters
            {
                RxAdjRssi = rxAdjRssi,
                Absorption = absorption
            };
        }

        return deviceParams;
    }

    private double CalculateError(List<Measure> nodes, Dictionary<string, DeviceParameters> deviceParams)
    {
        double totalError = 0;
        int count = 0;

        foreach (var node in nodes)
        {
            try
            {
                if (!deviceParams.TryGetValue(node.Rx.Id, out var rxParams) ||
                    !deviceParams.TryGetValue(node.Tx.Id, out var txParams))
                {
                    continue;
                }

                var distance = node.Rx.Location.DistanceTo(node.Tx.Location);
                var rxAdjRssi = rxParams.RxAdjRssi;
                var txAdjRssi = txParams.RxAdjRssi;

                // Use average of both device absorptions
                var absorption = (rxParams.Absorption + txParams.Absorption) / 2;

                // Safeguard against negative or zero absorption
                if (absorption <= 0.1)
                {
                    absorption = 0.1;
                }

                // Calculate distance based on RSSI
                var calculatedDistance = Math.Pow(10, (-59 + rxAdjRssi + txAdjRssi - node.Rssi) / (10.0d * absorption));

                // Skip invalid calculations
                if (double.IsNaN(calculatedDistance) || double.IsInfinity(calculatedDistance))
                {
                    continue;
                }

                // Squared error
                totalError += Math.Pow(distance - calculatedDistance, 2);
                count++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error calculating distance for node {Rx} to {Tx}", node.Rx.Id, node.Tx.Id);
            }
        }

        return count > 0 ? totalError / count : double.MaxValue;
    }
}