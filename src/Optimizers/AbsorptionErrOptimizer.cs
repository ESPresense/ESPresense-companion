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

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        var results = new OptimizationResults();

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.Where(b => b.Current).ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            double Distance(Vector<double> x, Measure dn) => Math.Pow(10, (dn.RefRssi - dn.Rssi) / (10.0d * x[0]));

            if (rxNodes.Length < 3) continue;
            var optimization = _state.Config?.Optimization;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] <= optimization?.AbsorptionMin || x[0] >= optimization?.AbsorptionMax) return double.PositiveInfinity;

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                        return error;
                    });

                var initialGuess = Vector<double>.Build.DenseOfArray(new[] {
                    ((optimization?.AbsorptionMax ?? 4.0) - (optimization?.AbsorptionMin ?? 2.0)) / 2 + (optimization?.AbsorptionMin ?? 2.0)
                });

                var solver = new NelderMeadSimplex(1e-4, 10000);
                var result = solver.FindMinimum(obj, initialGuess);

                var absorption = result.MinimizingPoint[0];
                if (absorption < optimization?.AbsorptionMin) continue;
                if (absorption > optimization?.AbsorptionMax) continue;
                results.RxNodes.Add(g.Key.Id, new ProposedValues { RxAdjRssi = null, Absorption = absorption, Error = result.FunctionInfoAtMinimum.Value });
            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", g.Key.Id, ex.Message);
            }
        }

        return results;
    }
}