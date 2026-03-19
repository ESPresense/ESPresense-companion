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

        if (optimization == null)
        {
            _logger.Warning("Optimization configuration not found");
            return results;
        }

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
            var (rxAdjRssiDict, pathAbsorptionDict, error1) = OptimizeRxAdjRssiAndPathAbsorption(allNodes, uniqueDeviceIds, optimization, existingSettings);

            // Step 2: Optimize node-specific absorptions while keeping RxAdjRssi constant
            var (deviceParams, error2) = OptimizeNodeAbsorptions(allNodes, uniqueDeviceIds, rxAdjRssiDict, pathAbsorptionDict, optimization, existingSettings);

            // Process and store results
            foreach (var deviceId in uniqueDeviceIds)
            {
                if (deviceParams.TryGetValue(deviceId, out var parameters))
                {
                    results.Nodes[deviceId] = new ProposedValues
                    {
                        RxAdjRssi = parameters.RxAdjRssi,
                        Absorption = parameters.Absorption,
                        Error = error2
                    };
                }
            }

            _logger.Information("Optimization completed. Step 1 error: {Error1}, Step 2 error: {Error2}", error1, error2);
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
                // Bounds should always come from global config
                double rxAdjMin = optimization.RxAdjRssiMin;
                double rxAdjMax = optimization.RxAdjRssiMax;
                if (rxAdjRssi < rxAdjMin || rxAdjRssi > rxAdjMax)
                    return double.PositiveInfinity;
                rxAdjRssiDict[uniqueDeviceIds[i]] = rxAdjRssi;
            }

            int offset = uniqueDeviceIds.Count;
            foreach (var pair in pathPairs)
            {
                var absorption = x[offset++];
                if (absorption <= optimization.AbsorptionMin || absorption >= optimization.AbsorptionMax)
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
            initialGuess[i] = Math.Clamp((optimization.AbsorptionMax - optimization.AbsorptionMin) / 2 + optimization.AbsorptionMin, optimization.AbsorptionMin, optimization.AbsorptionMax);
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

    private (Dictionary<string, DeviceParameters> DeviceParams, double Error) OptimizeNodeAbsorptions(
        List<Measure> allNodes,
        List<string> uniqueDeviceIds,
        Dictionary<string, double> rxAdjRssiDict,
        Dictionary<(string, string), double> pathAbsorptionDict,
        ConfigOptimization optimization,
        Dictionary<string, NodeSettings> existingSettings)
    {
        // Build initial guess vector: one absorption parameter per device
        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count);
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            existingSettings.TryGetValue(uniqueDeviceIds[i], out var nodeSettings);
            double absorptionMin = optimization.AbsorptionMin;
            double absorptionMax = optimization.AbsorptionMax;
            // Use existing absorption if available, otherwise midpoint
            initialGuess[i] = Math.Clamp(nodeSettings?.Calibration?.Absorption ?? ((absorptionMax - absorptionMin) / 2 + absorptionMin), absorptionMin, absorptionMax);
        }

        // Define objective function: given absorption vector x, compute total error
        double CalculateErrorFromAbsorptions(Vector<double> x)
        {
            // Build deviceParams from x (absorptions) combined with fixed rxAdjRssi and TxRefRssi from settings
            var deviceParams = new Dictionary<string, DeviceParameters>();
            for (int i = 0; i < uniqueDeviceIds.Count; i++)
            {
                var deviceId = uniqueDeviceIds[i];
                var absorption = x[i];
                absorption = Math.Clamp(absorption, optimization.AbsorptionMin, optimization.AbsorptionMax);
                existingSettings.TryGetValue(deviceId, out var nodeSettings);
                deviceParams[deviceId] = new DeviceParameters
                {
                    RxAdjRssi = rxAdjRssiDict[deviceId],
                    Absorption = absorption,
                    TxRefRssi = nodeSettings?.Calibration?.TxRefRssi
                };
            }

            return CalculateError(allNodes, rxAdjRssiDict, deviceParamsDict: deviceParams);
        }

        // Create objective with numeric gradient
        var objGradient = ObjectiveFunction.Gradient(
            x => CalculateErrorFromAbsorptions(x),
            x =>
            {
                var gradient = Vector<double>.Build.Dense(x.Count);
                double h = 1e-5;
                double baseError = CalculateErrorFromAbsorptions(x);

                for (int i = 0; i < x.Count; i++)
                {
                    var xPlus = x.Clone();
                    xPlus[i] += h;
                    double errorPlus = CalculateErrorFromAbsorptions(xPlus);
                    gradient[i] = (errorPlus - baseError) / h;
                }

                return gradient;
            }
        );

        try
        {
            // Bounds for absorption parameters
            var lowerBound = Vector<double>.Build.Dense(uniqueDeviceIds.Count, optimization.AbsorptionMin);
            var upperBound = Vector<double>.Build.Dense(uniqueDeviceIds.Count, optimization.AbsorptionMax);

            // Use bounded BFGS minimizer
            var solver = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000);
            var result = solver.FindMinimum(objGradient, lowerBound, upperBound, initialGuess);

            // Extract optimized parameters into DeviceParameters dictionary
            var deviceParams = new Dictionary<string, DeviceParameters>();
            for (int i = 0; i < uniqueDeviceIds.Count; i++)
            {
                var deviceId = uniqueDeviceIds[i];
                var absorption = result.MinimizingPoint[i];
                existingSettings.TryGetValue(deviceId, out var nodeSettings);
                deviceParams[deviceId] = new DeviceParameters
                {
                    RxAdjRssi = rxAdjRssiDict[deviceId], // from step 1
                    Absorption = absorption,
                    TxRefRssi = nodeSettings?.Calibration?.TxRefRssi
                };
            }

            _logger.Information("Node absorption optimization completed: Iterations={Iterations}, Status={Status}, Error={Error}",
                result.Iterations, result.ReasonForExit, result.FunctionInfoAtMinimum.Value);

            return (deviceParams, result.FunctionInfoAtMinimum.Value);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Node absorption optimization failed");

            // Return default values if optimization fails
            var defaultParams = new Dictionary<string, DeviceParameters>();
            foreach (var id in uniqueDeviceIds)
            {
                existingSettings.TryGetValue(id, out var nodeSettings);
                defaultParams[id] = new DeviceParameters
                {
                    RxAdjRssi = rxAdjRssiDict[id],
                    Absorption = (optimization.AbsorptionMax + optimization.AbsorptionMin) / 2,
                    TxRefRssi = nodeSettings?.Calibration?.TxRefRssi
                };
            }

            return (defaultParams, double.MaxValue);
        }
    }

    private double CalculateError(List<Measure> nodes, Dictionary<string, double> rxAdjRssiDict, Dictionary<(string, string), double>? pathAbsorptionDict = null, Dictionary<string, DeviceParameters>? deviceParamsDict = null)
    {
        double totalError = 0;
        int count = 0;

        foreach (var node in nodes)
        {
            try
            {
                if (!rxAdjRssiDict.TryGetValue(node.Rx.Id, out var rxAdjRssi))
                    continue;

                // Get absorption: either per-device from deviceParamsDict or per-path from pathAbsorptionDict
                double absorption;
                if (deviceParamsDict != null && deviceParamsDict.TryGetValue(node.Rx.Id, out var rxDeviceParams))
                {
                    absorption = rxDeviceParams.Absorption;
                }
                else if (pathAbsorptionDict != null)
                {
                    var key = (Min(node.Rx.Id, node.Tx.Id), Max(node.Rx.Id, node.Tx.Id));
                    if (!pathAbsorptionDict.TryGetValue(key, out absorption))
                        continue;
                }
                else
                {
                    continue;
                }

                // Get TxRefRssi
                double? txRefRssi = null;
                if (deviceParamsDict != null && deviceParamsDict.TryGetValue(node.Tx.Id, out var txDeviceParams))
                {
                    txRefRssi = txDeviceParams.TxRefRssi;
                }
                // Fallback: could also try to get from existingSettings but not passed here; use default
                txRefRssi ??= -59;

                var distance = node.Rx.Location.DistanceTo(node.Tx.Location);

                // Safeguard against negative or zero absorption
                if (absorption <= 0.1)
                {
                    absorption = 0.1;
                }

                // Calculate distance based on RSSI
                var calculatedDistance = Math.Pow(10, (txRefRssi.Value + rxAdjRssi - node.Rssi) / (10.0d * absorption));

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

    private Dictionary<string, DeviceParameters> CreateDeviceParamsFromVector(Vector<double> x, List<string> uniqueDeviceIds, ConfigOptimization optimization)
    {
        var deviceParams = new Dictionary<string, DeviceParameters>();

        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            var rxAdjRssi = x[i];
            var absorption = x[i + uniqueDeviceIds.Count];

            // Enforce constraints by clamping values to valid ranges
            rxAdjRssi = Math.Clamp(rxAdjRssi,
                optimization.RxAdjRssiMin,
                optimization.RxAdjRssiMax);

            absorption = Math.Clamp(absorption,
                optimization.AbsorptionMin,
                optimization.AbsorptionMax);

            deviceParams[uniqueDeviceIds[i]] = new DeviceParameters
            {
                RxAdjRssi = rxAdjRssi,
                Absorption = absorption,
                TxRefRssi = null // Not stored in this method; only RxAdj and Absorption are optimized
            };
        }

        return deviceParams;
    }

    private T Min<T>(T a, T b) where T : IComparable<T>
    {
        return a.CompareTo(b) < 0 ? a : b;
    }

    private T Max<T>(T a, T b) where T : IComparable<T>
    {
        return a.CompareTo(b) > 0 ? a : b;
    }

    // Nested class to hold device parameters
    private class DeviceParameters
    {
        public double RxAdjRssi { get; set; }
        public double Absorption { get; set; }
        public double? TxRefRssi { get; set; }
    }
}
