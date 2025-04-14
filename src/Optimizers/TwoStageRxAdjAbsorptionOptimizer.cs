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

    /// <summary>
    /// Optimizes radio signal parameters using a two-stage process.
    /// </summary>
    /// <remarks>
    /// The method retrieves configuration settings to establish bounds for the Rx RSSI adjustment and absorption parameters. It processes groups of nodes from the provided snapshot (ignoring groups with fewer than three nodes). In stage 1, it calibrates the Rx RSSI adjustment using nearby nodes (or all nodes if no close ones are found) with a fixed absorption value. In stage 2, it refines the absorption parameter based on all nodes with the previously optimized Rx RSSI adjustment. Optimization is performed using the Nelder–Mead simplex algorithm, and results are clamped within the specified bounds. Exceptions during group processing are caught and logged, while an InvalidOperationException is thrown if the required optimization configuration is missing.
    /// </remarks>
    /// <param name="os">A snapshot representing the current node groups to optimize.</param>
    /// <returns>
    /// An OptimizationResults object mapping node identifiers to their proposed values, including the optimized Rx RSSI adjustment, absorption, and error measurement.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the optimization configuration is not found in the current state.
    /// </exception>
    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        OptimizationResults or = new();
        ConfigOptimization optimization = _state.Config?.Optimization ?? throw new InvalidOperationException("Optimization config not found");

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            if (rxNodes.Length < 3) continue;

            // Get node-specific settings, fallback to global config if not found
            existingSettings.TryGetValue(g.Key.Id, out var nodeSettings);

            // Bounds should always come from global config
            double rxAdjMin = optimization.RxAdjRssiMin;
            double rxAdjMax = optimization.RxAdjRssiMax;
            double absorptionMin = optimization.AbsorptionMin;
            double absorptionMax = optimization.AbsorptionMax;
            double absorptionMiddle = absorptionMin + (absorptionMax - absorptionMin) / 2; // Used for fixed absorption in stage 1

            Log.Information("Bounds: RxAdj [{0}, {1}], Absorption [{2}, {3}]", rxAdjMin, rxAdjMax, absorptionMin, absorptionMax);

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

                // Initial guess for RxAdj uses node setting if available, else midpoint of global bounds
                var initialRxAdjGuessValue = nodeSettings?.Calibration?.RxAdjRssi ?? (rxAdjMax - rxAdjMin) / 2 + rxAdjMin;
                var initialGuessRxAdj = Vector<double>.Build.DenseOfArray(new[] { initialRxAdjGuessValue });
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

                // Initial guess for Absorption uses node setting if available, else midpoint of global bounds
                var initialAbsGuessValue = nodeSettings?.Calibration?.Absorption ?? absorptionMiddle;
                var initialGuessAbs = Vector<double>.Build.DenseOfArray(new[] { initialAbsGuessValue });
                var solverAbs = new NelderMeadSimplex(1e-7, 1000);
                var resultAbs = solverAbs.FindMinimum(objAbs, initialGuessAbs);
                var absorption = Math.Clamp(resultAbs.MinimizingPoint[0], absorptionMin, absorptionMax);

                Log.Information("Optimized {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3:0.0}",
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