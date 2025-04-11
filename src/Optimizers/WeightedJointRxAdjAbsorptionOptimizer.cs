using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class WeightedJointRxAdjAbsorptionOptimizer : IOptimizer
{
    private readonly State _state;

    public WeightedJointRxAdjAbsorptionOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Weighted Joint RxAdj & Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        OptimizationResults or = new();
        var optimization = _state.Config?.Optimization;
        var rxAdjMin = optimization?.RxAdjRssiMin ?? -15; // Match logs
        var rxAdjMax = optimization?.RxAdjRssiMax ?? 25;
        var absorptionMin = optimization?.AbsorptionMin ?? 2.5;
        var absorptionMax = optimization?.AbsorptionMax ?? 3.5;
        Log.Information("Bounds: RxAdj [{0}, {1}], Absorption [{2}, {3}]", rxAdjMin, rxAdjMax, absorptionMin, absorptionMax);

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.Where(b => b.Current).ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            Log.Debug("Node {0}: {1} measurements", g.Key.Id, rxNodes.Length);
            foreach (var (dn, i) in rxNodes.Select((dn, i) => (dn, i)))
                Log.Debug("Node {0}: pos[{1}]={2:0.00}m, Rssi={3}", g.Key.Id, i, pos[i], dn.Rssi);

            double Distance(Vector<double> x, Measure dn)
            {
                double exponent = (-59 + x[0] - dn.Rssi) / (10.0d * x[1]);
                return (x[1] > 0 && !double.IsInfinity(exponent)) ? Math.Pow(10, exponent) : double.MaxValue;
            }

            if (rxNodes.Length < 3) continue;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < rxAdjMin || x[0] > rxAdjMax || x[1] < absorptionMin || x[1] > absorptionMax)
                            return double.PositiveInfinity;

                        double error = 0;
                        for (int i = 0; i < rxNodes.Length; i++)
                        {
                            double d = pos[i];
                            double d0 = 3.0; // Slower decay for RxAdj
                            double wRx = 1 / (1 + Math.Pow(d / d0, 2));
                            double wAbsRaw = Math.Pow(d / d0, 2) / (1 + Math.Pow(d / d0, 2));
                            double wAbs = Math.Min(2.0, 2.0 * wAbsRaw); // Boost far nodes, cap at 2
                            double distError = pos[i] - Distance(x, rxNodes[i]);
                            error += (wRx + wAbs) * distError * distError;
                            Log.Debug("Node {0}: d={1:0.00}, wRx={2:0.00}, wAbs={3:0.00}, distErr={4:0.00}",
                                g.Key.Id, d, wRx, wAbs, distError);
                        }
                        return error / rxNodes.Length;
                    });

                var initialRxAdj = 0;
                var initialGuess = Vector<double>.Build.DenseOfArray(new[] { initialRxAdj, 2.85 });
                Log.Information("Node {0}: Starting RxAdj={1:0.00}, Abs={2:0.00}", g.Key.Id, initialGuess[0], initialGuess[1]);

                var solver = new NelderMeadSimplex(1e-6, 10000);
                var result = solver.FindMinimum(obj, initialGuess);

                var rxAdjRssi = Math.Clamp(result.MinimizingPoint[0], rxAdjMin, rxAdjMax);
                var absorption = Math.Clamp(result.MinimizingPoint[1], absorptionMin, absorptionMax);

                Log.Information("Optimized {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3}",
                    g.Key.Id, rxAdjRssi, absorption, result.FunctionInfoAtMinimum.Value);

                or.Nodes.Add(g.Key.Id, new ProposedValues
                {
                    RxAdjRssi = rxAdjRssi,
                    Absorption = absorption,
                    Error = result.FunctionInfoAtMinimum.Value
                });
            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", g.Key.Id, ex.Message);
            }
        }

        return or;
    }
}