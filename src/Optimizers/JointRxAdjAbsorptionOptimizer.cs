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

    /// <summary>
    /// Optimizes radio receiver adjustment (RxAdjRssi) and absorption parameters for each receiver group in the provided snapshot.
    /// </summary>
    /// <param name="os">The snapshot containing groups of receiver nodes to process for optimization.</param>
    /// <returns>
    /// An <see cref="OptimizationResults"/> object aggregating the optimized values for each receiver group.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the optimization configuration is not available in the state.
    /// </exception>
    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        OptimizationResults or = new();
        ConfigOptimization optimization = _state.Config?.Optimization ?? throw new InvalidOperationException("Optimization config not found");

        foreach (var g in os.ByRx())
        {
            var rxNodes = g.ToArray();
            var pos = rxNodes.Select(n => n.Rx.Location.DistanceTo(n.Tx.Location)).ToArray();

            // Get node-specific settings, fallback to global config if not found
            existingSettings.TryGetValue(g.Key.Id, out var nodeSettings);

            // Bounds should always come from global config
            double rxAdjMin = optimization.RxAdjRssiMin;
            double rxAdjMax = optimization.RxAdjRssiMax;
            double absorptionMin = optimization.AbsorptionMin;
            double absorptionMax = optimization.AbsorptionMax;
            double absorptionMiddle = absorptionMin + (absorptionMax - absorptionMin) / 2; // Used for regularization and initial guess fallback

            double Distance(Vector<double> x, Measure dn) => Math.Pow(10, (-60 + x[0] - dn.Rssi) / (10.0d * x[1]));

            if (rxNodes.Length < 3) continue;

            try
            {
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        if (x[0] < rxAdjMin || x[0] > rxAdjMax)
                        {
                            Log.Debug("RxAdjRssi OOB {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}", g.Key.Id, x[0], x[1]);
                            return double.PositiveInfinity;
                        }
                        if (x[1] < absorptionMin || x[1] > absorptionMax)
                        {
                            Log.Debug("Absorption OOB {0,-20}: RxAdj: {1:0.00} dBm, Absorption: {2:0.00}", g.Key.Id, x[0], x[1]);
                            return double.PositiveInfinity;
                        }

                        var error = rxNodes
                            .Select((dn, i) => new { err = pos[i] - Distance(x, dn), weight = 1 })
                            .Average(a => a.weight * Math.Pow(a.err, 4));

                        error += 0.25 * ( Math.Abs(x[0]) + Math.Pow(x[1] - absorptionMiddle, 2));

                        Log.Debug("Optimized {0,-20}     : RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3:0.0}", g.Key.Id, x[0], x[1], error);
                        return error;
                    });

                // Initial guess uses node settings if available, else global bounds/midpoint
                var initialRxAdjGuess = nodeSettings?.Calibration?.RxAdjRssi ?? rxAdjMin; // Fallback to min bound
                var initialAbsGuess = nodeSettings?.Calibration?.Absorption ?? absorptionMiddle;
                var initialGuess = Vector<double>.Build.DenseOfArray(new[] { initialRxAdjGuess, initialAbsGuess });
                var initialPert = Vector<double>.Build.DenseOfArray(new[] { rxAdjMax, absorptionMiddle }); // Perturbation uses global bounds

                var solver = new NelderMeadSimplex(1e-9, 10000);
                var result = solver.FindMinimum(obj, initialGuess, initialPert);

                var rxAdjRssi = Math.Clamp(result.MinimizingPoint[0], rxAdjMin, rxAdjMax);
                var absorption = Math.Clamp(result.MinimizingPoint[1], absorptionMin, absorptionMax);

                Log.Information("Optimized {0,-20}     : RxAdj: {1:0.00} dBm, Absorption: {2:0.00}, Error: {3:0.0}",
                    g.Key.Id, rxAdjRssi, absorption, result.FunctionInfoAtMinimum.Value);

                or.Nodes.Add(g.Key.Id, new ProposedValues
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