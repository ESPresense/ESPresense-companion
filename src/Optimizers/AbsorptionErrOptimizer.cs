using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class AbsorptionErrOptimizer : IOptimizer
{
    private readonly State _state;

    public AbsorptionErrOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Absorption NelderMead";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            double Distance(Vector<double> x, Measure dn) => Math.Pow(10, (dn.RefRssi - dn.Rssi) / (10.0d * x[0]));

            if (rxNodes.Length < 3) continue;

            // Get node-specific settings, fallback to global config if not found
            existingSettings.TryGetValue(g.Key.Id, out var nodeSettings);
            var optimization = _state.Config?.Optimization;

            // Bounds should always come from global config
            double absorptionMin = optimization?.AbsorptionMin ?? 1.0;
            double absorptionMax = optimization?.AbsorptionMax ?? 5.0;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] <= absorptionMin || x[0] >= absorptionMax) return double.PositiveInfinity;

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                        return error;
                    });

                // Initial guess uses node setting if available, else midpoint of global bounds
                var initialGuessValue = nodeSettings?.Calibration?.Absorption ?? (absorptionMax - absorptionMin) / 2 + absorptionMin;
                var initialGuess = Vector<double>.Build.DenseOfArray(new[] { initialGuessValue });

                var solver = new NelderMeadSimplex(1e-4, 10000);
                var result = solver.FindMinimum(obj, initialGuess);

                var absorption = result.MinimizingPoint[0];
                if (absorption < absorptionMin) continue;
                if (absorption > absorptionMax) continue;
                results.Nodes.Add(g.Key.Id, new ProposedValues { RxAdjRssi = null, Absorption = absorption, Error = result.FunctionInfoAtMinimum.Value });
            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", g.Key.Id, ex.Message);
            }
        }

        return results;
    }
}