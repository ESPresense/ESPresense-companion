using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using Serilog;

namespace ESPresense.Optimizers;

public class TwoStageRxAdjAbsorptionOptimizer : IOptimizer
{
    private readonly State _state;

    public TwoStageRxAdjAbsorptionOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Two-Stage RxAdj & Absorption";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        OptimizationResults or = new();
        ConfigOptimization optimization = _state.Config?.Optimization ?? throw new InvalidOperationException("Optimization config not found");

        var rxAdjMin = optimization.RxAdjRssiMin;
        var rxAdjMax = optimization.RxAdjRssiMax;
        var absorptionMin = optimization.AbsorptionMin;
        var absorptionMax = optimization.AbsorptionMax;
        var absorptionMiddle = optimization.AbsorptionMin + (optimization.AbsorptionMax - optimization.AbsorptionMin) / 2;

        Log.Information("Bounds: RxAdj [{0}, {1}], Absorption [{2}, {3}]", rxAdjMin, rxAdjMax, absorptionMin, absorptionMax);

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            if (rxNodes.Length < 3) continue;

            try
            {
                // Stage 1: Optimize RxAdjRssi with fixed absorption, close nodes only
                double fixedAbsorption = absorptionMiddle;
                var closeNodes = rxNodes.Zip(pos, (n, d) => new { Node = n, Distance = d })
                                       .Where(x => x.Distance <= 5.0)
                                       .ToArray();
                var nodesToUse = closeNodes.Length > 0 ? closeNodes : rxNodes.Zip(pos, (n, d) => new { Node = n, Distance = d }).ToArray();
                var posToUse = nodesToUse.Select(x => x.Distance).ToArray();

                var objRxAdj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < rxAdjMin || x[0] > rxAdjMax) return double.PositiveInfinity;
                        double error = 0;
                        for (int i = 0; i < nodesToUse.Length; i++)
                        {
                            double dist = Math.Pow(10, (-59 + x[0] - nodesToUse[i].Node.Rssi) / (10.0 * fixedAbsorption));
                            double distError = posToUse[i] - dist;
                            error += distError * distError;
                        }
                        return error / nodesToUse.Length;
                    });

                var initialRxAdj = 0;
                var initialGuessRxAdj = Vector<double>.Build.DenseOfArray(new[] { (double)initialRxAdj });
                var solverRxAdj = new NelderMeadSimplex(1e-3, 1000);
                var resultRxAdj = solverRxAdj.FindMinimum(objRxAdj, initialGuessRxAdj);
                var rxAdjRssi = Math.Clamp(resultRxAdj.MinimizingPoint[0], rxAdjMin, rxAdjMax);
                Log.Information("Node {0}: Stage 1 RxAdj={1:0.00}, Error={2}, Used {3} nodes", g.Key.Id, rxAdjRssi, resultRxAdj.FunctionInfoAtMinimum.Value, nodesToUse.Length);

                // Stage 2: Optimize absorption with fixed RxAdjRssi, all nodes
                var objAbs = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < absorptionMin || x[0] > absorptionMax) return double.PositiveInfinity;
                        double error = 0;
                        for (int i = 0; i < rxNodes.Length; i++)
                        {
                            double d = pos[i];
                            double wAbs = Math.Min(2.0, 2.0 * Math.Pow(d / 3.0, 2) / (1 + Math.Pow(d / 3.0, 2)));
                            double dist = Math.Pow(10, (-59 + rxAdjRssi - rxNodes[i].Rssi) / (10.0 * x[0]));
                            double distError = pos[i] - dist;
                            double predictedRssi = -59 + rxAdjRssi - 10 * x[0] * Math.Log10(pos[i]);
                            Log.Debug("Node {0}: d={1:0.00}, wAbs={2:0.00}, distErr={3:0.00}, PredRssi={4:0.00}, MeasRssi={5}",
                                g.Key.Id, d, wAbs, distError, predictedRssi, rxNodes[i].Rssi);
                            error += wAbs * distError * distError;
                        }
                        return error / rxNodes.Length;
                    });

                var initialGuessAbs = Vector<double>.Build.DenseOfArray(new[] { 2.85 });
                var solverAbs = new NelderMeadSimplex(1e-7, 1000);
                var resultAbs = solverAbs.FindMinimum(objAbs, initialGuessAbs);
                var absorption = Math.Clamp(resultAbs.MinimizingPoint[0], absorptionMin, absorptionMax); // Fixed index from [1] to [0]

                Log.Information("Optimized {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3}",
                    g.Key.Id, rxAdjRssi, absorption, resultAbs.FunctionInfoAtMinimum.Value);

                or.Nodes.Add(g.Key.Id, new ProposedValues
                {
                    RxAdjRssi = rxAdjRssi,
                    Absorption = absorption,
                    Error = resultAbs.FunctionInfoAtMinimum.Value
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