using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

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

    public OptimizationResults Optimize(OptimizationSnapshot os)
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
            // Use a simpler one-step optimization approach that's more likely to converge
            var (deviceParams, error) = OptimizeDeviceParameters(allNodes, uniqueDeviceIds, optimization);

            // Store results
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

    private class DeviceParameters
    {
        public double RxAdjRssi { get; set; }
        public double Absorption { get; set; }
    }

    private (Dictionary<string, DeviceParameters> DeviceParams, double Error)
        OptimizeDeviceParameters(List<Measure> allNodes, List<string> uniqueDeviceIds, ConfigOptimization optimization)
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
                    estimatedAbsorption = Math.Clamp(
                        (-59 - avgRssi) / (10 * Math.Log10(avgDistance)),
                        optimization?.AbsorptionMin ?? 1.5,
                        optimization?.AbsorptionMax ?? 4.5);
                }
            }

            // Add jitter to avoid all devices starting with identical values
            var random = new Random(i); // Use device index as seed for reproducibility
            double jitter = random.NextDouble() * 0.1 - 0.05; // Small random adjustment ±0.05

            // Initialize RxAdjRssi with estimate plus small random jitter
            initialGuess[i] = estimatedRxAdjRssi + jitter;

            // Initialize Absorption with estimate plus small random jitter
            initialGuess[i + uniqueDeviceIds.Count] = estimatedAbsorption + jitter;
        }

        // Use Conjugate Gradient method which can work better for some problems
        var objGradient = ObjectiveFunction.Gradient(
            // Function to compute value
            x => {
                try
                {
                    var deviceParams = CreateDeviceParamsFromVector(x, uniqueDeviceIds, optimization);
                    return CalculateError(allNodes, deviceParams);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Error in objective function calculation");
                    return double.MaxValue;
                }
            },
            // Function to compute gradient
            x => {
                try
                {
                    var baseParams = CreateDeviceParamsFromVector(x, uniqueDeviceIds, optimization);
                    var baseError = CalculateError(allNodes, baseParams);

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
                    _logger.Warning(ex, "Error in gradient calculation");
                    return Vector<double>.Build.Dense(x.Count);
                }
            }
        );

        // ConjugateGradientMinimizer only takes 3 tolerance parameters, not a maximum iteration count
        var solver = new ConjugateGradientMinimizer(1e-3, 1000);

        MinimizationResult result;
        try
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