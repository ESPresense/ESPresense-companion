using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;
using System.Collections.Generic;
using System.Linq;

namespace ESPresense.Optimizers;

public class CombinedOptimizer : IOptimizer
{
    private readonly State _state;

    public CombinedOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Two-Step Optimized Combined RxAdjRssi and Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        var results = new OptimizationResults();
        var optimization = _state.Config?.Optimization;

        var allNodes = os.ByRx().SelectMany(g => g).Where(b => b.Current).ToList();
        var uniqueDeviceIds = allNodes.SelectMany(n => new[] { n.Rx.Id, n.Tx.Id }).Distinct().ToList();

        if (allNodes.Count < 3) return results;

        try
        {
            // Step 1: Optimize RxAdjRssi and path-specific absorptions
            var (rxAdjRssiDict, pathAbsorptionDict, error) = OptimizeRxAdjRssiAndPathAbsorption(allNodes, uniqueDeviceIds, optimization);

            // Step 2: Optimize node-specific absorptions while keeping RxAdjRssi constant
            var nodeAbsorptions = OptimizeNodeAbsorptions(allNodes, uniqueDeviceIds, rxAdjRssiDict, pathAbsorptionDict, optimization);

            // Process and store results
            foreach (var deviceId in uniqueDeviceIds)
            {
                if (rxAdjRssiDict.TryGetValue(deviceId, out var rxAdjRssi) && 
                    nodeAbsorptions.TryGetValue(deviceId, out var absorption))
                {
                    results.RxNodes[deviceId] = new ProposedValues
                    {
                        RxAdjRssi = rxAdjRssi,
                        Absorption = absorption,
                        Error = error
                    };
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in combined optimization: {0}", ex.Message);
        }

        return results;
    }

    private (Dictionary<string, double> RxAdjRssi, Dictionary<(string, string), double> PathAbsorption, double Error) 
        OptimizeRxAdjRssiAndPathAbsorption(List<Measure> allNodes, List<string> uniqueDeviceIds, ConfigOptimization optimization)
    {
        var pathPairs = new HashSet<(string, string)>(allNodes.Select(n => (Min(n.Rx.Id, n.Tx.Id), Max(n.Rx.Id, n.Tx.Id))));

        var obj = ObjectiveFunction.Value(x =>
        {
            var rxAdjRssiDict = new Dictionary<string, double>();
            var absorptionDict = new Dictionary<(string, string), double>();

            for (int i = 0; i < uniqueDeviceIds.Count; i++)
            {
                var rxAdjRssi = x[i];
                if (rxAdjRssi < optimization?.RxAdjRssiMin || rxAdjRssi > optimization?.RxAdjRssiMax)
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
            initialGuess[i] = 0; // Initial guess for RxAdjRssi
        }
        for (int i = uniqueDeviceIds.Count; i < initialGuess.Count; i++)
        {
            initialGuess[i] = (optimization?.AbsorptionMax - optimization?.AbsorptionMin) / 2 + optimization?.AbsorptionMin ?? 3d;
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
        Dictionary<string, double> rxAdjRssiDict, Dictionary<(string, string), double> pathAbsorptionDict, ConfigOptimization optimization)
    {
        var obj = ObjectiveFunction.ValueAndGradient(x =>
        {
            // Objective function (error calculation) remains the same
            var value = CalculateError(allNodes, rxAdjRssiDict, pathAbsorptionDict: absorptionDict);
            
            // Numerically approximate the gradient
            var gradient = new double[x.Count];
            double epsilon = 1e-5;
            
            for (int i = 0; i < x.Count; i++)
            {
                var xPlusEps = x.Clone();
                xPlusEps[i] += epsilon;
                
                var errorPlusEps = CalculateError(allNodes, rxAdjRssiDict, pathAbsorptionDict: absorptionDict);
                gradient[i] = (errorPlusEps - value) / epsilon;
            }

            return (value, Vector<double>.Build.Dense(gradient));
        });

        var initialGuess = Vector<double>.Build.Dense(uniqueDeviceIds.Count, 
            (optimization?.AbsorptionMax - optimization?.AbsorptionMin) / 2 + optimization?.AbsorptionMin ?? 3d);

        var solver = new BfgsMinimizer(1e-7, 1e-7, 1e-7);
        var result = solver.FindMinimum(obj, initialGuess);

        var nodeAbsorptions = new Dictionary<string, double>();
        for (int i = 0; i < uniqueDeviceIds.Count; i++)
        {
            nodeAbsorptions[uniqueDeviceIds[i]] = result.MinimizingPoint[i];
        }

        return nodeAbsorptions;
    }

    private double CalculateError(List<Measure> nodes, Dictionary<string, double> rxAdjRssiDict, 
        Dictionary<string, double> nodeAbsorptionDict = null, Dictionary<(string, string), double> pathAbsorptionDict = null)
    {
        return nodes.Select(n =>
        {
            var distance = n.Rx.Location.DistanceTo(n.Tx.Location);
            var rxAdjRssi = rxAdjRssiDict[n.Rx.Id];
            var txAdjRssi = rxAdjRssiDict[n.Tx.Id];
            double absorption;

            if (pathAbsorptionDict != null)
            {
                var pathKey = (Min(n.Rx.Id, n.Tx.Id), Max(n.Rx.Id, n.Tx.Id));
                absorption = pathAbsorptionDict[pathKey];
            }
            else if (nodeAbsorptionDict != null)
            {
                absorption = (nodeAbsorptionDict[n.Rx.Id] + nodeAbsorptionDict[n.Tx.Id]) / 2;
            }
            else
            {
                throw new ArgumentException("Either nodeAbsorptionDict or pathAbsorptionDict must be provided");
            }

            var calculatedDistance = Math.Pow(10, (-59 + rxAdjRssi + txAdjRssi - n.Rssi) / (10.0d * absorption));
            return Math.Pow(distance - calculatedDistance, 2);
        }).Average();
    }

    private static string Min(string a, string b) => string.Compare(a, b) < 0 ? a : b;
    private static string Max(string a, string b) => string.Compare(a, b) >= 0 ? a : b;
}