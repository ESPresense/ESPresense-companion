﻿using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class RxAdjRssiOptimizer : IOptimizer
{
    private readonly State _state;

    public RxAdjRssiOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Rx Adj Rssi";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {

        OptimizationResults or = new();
        var optimization = _state.Config?.Optimization;

        var absorption = ((optimization?.AbsorptionMax - optimization?.AbsorptionMin) / 2d) + optimization?.AbsorptionMin ?? 3d;

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            double Distance(Vector<double> x, Measure dn) => Math.Pow(10, (-59 + x[0] - dn.Rssi ) / (10.0d * absorption));

            if (rxNodes.Length < 3) continue;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < optimization?.RxAdjRssiMin || x[0] > optimization?.RxAdjRssiMax) return double.PositiveInfinity;

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                        return error;
                    });

                // Get node settings and bounds inside the try block to ensure scope
                existingSettings.TryGetValue(g.Key.Id, out var nodeSettings);
                double rxAdjMin = optimization?.RxAdjRssiMin ?? -15;
                double rxAdjMax = optimization?.RxAdjRssiMax ?? 25;

                // Initial guess uses node setting if available, else 0
                var initialGuessValue = nodeSettings?.Calibration?.RxAdjRssi ?? 0d;
                // Clamp initial guess within global bounds
                initialGuessValue = Math.Clamp(initialGuessValue, rxAdjMin, rxAdjMax);
                // Explicitly create double array
                var initialGuess = Vector<double>.Build.DenseOfArray(new double[] { initialGuessValue });

                var solver = new NelderMeadSimplex(1e-7, 1000);
                var result = solver.FindMinimum(obj, initialGuess);
                var rxAdjRssi = result.MinimizingPoint[0];
                if (rxAdjRssi < optimization?.RxAdjRssiMin) rxAdjRssi = optimization.RxAdjRssiMin;
                if (rxAdjRssi > optimization?.RxAdjRssiMax) rxAdjRssi = optimization.RxAdjRssiMax;
                or.Nodes.Add(g.Key.Id, new ProposedValues { RxAdjRssi = rxAdjRssi, Absorption = absorption, Error = result.FunctionInfoAtMinimum.Value });
            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", g.Key.Id, ex.Message);
            }
        }

        return or;
    }
}