using ESPresense.Models;
using MathNet.Numerics;

namespace ESPresense.Optimizers;

public class AbsorptionLineFitOptimizer : IOptimizer
{
    private readonly State _state;

    public AbsorptionLineFitOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Absorption Line Fit";

    public OptimizationResults Optimize(OptimizationSnapshot os)
    {
        var or = new OptimizationResults();

        foreach (var g in os.ByRx())
        {
            var distances = new List<double>();
            var rssiDiffs = new List<double>();

            foreach (var m in g)
            {
                double distance = m.Rx.Location.DistanceTo(m.Tx.Location);
                distances.Add(Math.Log10(distance));

                double rssiDiff = m.RefRssi - m.Rssi;
                rssiDiffs.Add(rssiDiff);
            }

            if (distances.Count < 2)
                continue;

            var linearModel = Fit.Line(distances.ToArray(), rssiDiffs.ToArray());

            double absorption = linearModel.Item2 / 10;
            if (absorption < _state.Config?.Optimization.AbsorptionMin) continue;
            if (absorption > _state.Config?.Optimization.AbsorptionMax) continue;
            or.RxNodes.Add(g.Key.Id, new ProposedValues { Absorption = absorption });
        }

        return or;
    }
}