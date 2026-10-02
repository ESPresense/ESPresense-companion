using ESPresense.Models;

namespace ESPresense.Optimizers;

public class AbsorptionAvgOptimizer : IOptimizer
{
    private readonly State _state;

    public AbsorptionAvgOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Absorption Avg";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();

        // Resolve the limits once, falling back to ConfigOptimization's built-in defaults.
        // Comparing against `_state.Config?.Optimization.AbsorptionMin` (a double?) is always
        // false when config is null, which silently disabled the bounds check.
        var optimization = _state.Config?.Optimization ?? new ConfigOptimization();

        foreach (var g in os.ByRx())
        {
            var pathLossExponents = new List<double>();
            foreach (var m in g)
            {
                double distance = m.Rx.Location.DistanceTo(m.Tx.Location);

                // The path-loss model is rssi = ref - 10*n*log10(d). Solving for n divides by
                // log10(d), which is 0 at d == 1 (division by zero) and negative below 1 m (the
                // sign of n flips), so measurements at or under 1 m carry no usable information
                // about n. NaN distances fail the comparison and are skipped too.
                if (!(distance > 1.0) || double.IsInfinity(distance))
                    continue;

                double rssiDiff = m.Rssi - m.RefRssi;
                double pathLossExponent = -rssiDiff / (10 * Math.Log10(distance));
                if (!double.IsFinite(pathLossExponent))
                    continue;

                pathLossExponents.Add(pathLossExponent);
            }
            if (pathLossExponents.Count > 0)
            {
                var absorption = pathLossExponents.Average();
                if (!double.IsFinite(absorption)) continue;
                if (absorption < optimization.AbsorptionMin) continue;
                if (absorption > optimization.AbsorptionMax) continue;
                results.Nodes.Add(g.Key.Id, new ProposedValues { Absorption = absorption });
            }
        }

        return results;
    }
}