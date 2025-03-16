using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class JointRxAdjAbsorptionOptimizer : IOptimizer
{
    private readonly State _state;

    public JointRxAdjAbsorptionOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Joint RxAdj & Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        OptimizationResults or = new();
        ConfigOptimization optimization = _state.Config?.Optimization ?? throw new InvalidOperationException("Optimization config not found");

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.Where(b => b.Current).ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();
            var absorptionMiddle = optimization.AbsorptionMin + (optimization.AbsorptionMax - optimization.AbsorptionMin) / 2;

            double Distance(Vector<double> x, Measure dn) => Math.Pow(10, (-60 + x[0] - dn.Rssi) / (10.0d * x[1]));

            if (rxNodes.Length < 3) continue;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < optimization!.RxAdjRssiMin || x[0] > optimization.RxAdjRssiMax)
                        {
                            Log.Debug("RxAdjRssi OOB {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}", g.Key.Id, x[0], x[1]);
                            return double.PositiveInfinity;

                        }
                        if (x[1] < optimization.AbsorptionMin || x[1] > optimization.AbsorptionMax)
                        {
                            Log.Debug("Absorption OOB {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}", g.Key.Id, x[0], x[1]);
                            return double.PositiveInfinity;
                        }

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 4));

                        error += 0.25 * ( Math.Abs(x[0]) + Math.Pow(x[1] - absorptionMiddle, 2));

                        Log.Debug("Optimized {0,-20}     : RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3}", g.Key.Id, x[0], x[1], error);
                        return error;
                    });

                var initialGuess = Vector<double>.Build.DenseOfArray(new[] { optimization.RxAdjRssiMin, absorptionMiddle });
                var initialPert = Vector<double>.Build.DenseOfArray(new[] { optimization.RxAdjRssiMax, absorptionMiddle });

                var solver = new NelderMeadSimplex(1e-9, 10000);
                var result = solver.FindMinimum(obj, initialGuess, initialPert);

                var rxAdjRssi = Math.Clamp(result.MinimizingPoint[0], optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);
                var absorption = Math.Clamp(result.MinimizingPoint[1], optimization.AbsorptionMin, optimization.AbsorptionMax);

                Log.Information("Optimized {0,-20}     : RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3}",
                    g.Key.Id, rxAdjRssi, absorption, result.FunctionInfoAtMinimum.Value);

                or.RxNodes.Add(g.Key.Id, new ProposedValues
                {
                    RxAdjRssi = rxAdjRssi,
                    Absorption = absorption,
                    Error = result.FunctionInfoAtMinimum.Value
                });
            }
            catch (MaximumIterationsException ex)
            {
                Log.Error("Non-convergence for {0}, ", g.Key.Id);

            }
            catch (Exception ex)
            {
                Log.Error("Error optimizing {0}: {1}", g.Key.Id, ex.Message);
            }
        }

        return or;
    }
}