﻿﻿using ESPresense.Models;

namespace ESPresense.Optimizers;

public class AbsorptionAvgOptimizer : IOptimizer
{
    private readonly State _state;

    public AbsorptionAvgOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Absorption Avg";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        var results = new OptimizationResults();

        foreach (var g in os.ByRx())
        {
            var pathLossExponents = new List<double>();
            foreach (var m in g)
            {
                double distance = m.Rx.Location.DistanceTo(m.Tx.Location);

                double rssiDiff = m.Rssi - m.RefRssi;
                double pathLossExponent = -rssiDiff / (10 * Math.Log10(distance));

                pathLossExponents.Add(pathLossExponent);
            }
            if (pathLossExponents.Count > 0)
            {
                var absorption = pathLossExponents.Average();
                var optimization = _state.Config?.Optimization;
                if (absorption < (optimization?.AbsorptionMin ?? 2.0)) continue;
                if (absorption > (optimization?.AbsorptionMax ?? 4.0)) continue;
                results.RxNodes.Add(g.Key.Id, new ProposedValues { Absorption = absorption });
            }
        }

        return results;
    }
}