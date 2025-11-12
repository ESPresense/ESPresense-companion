using ESPresense.Models;
using ESPresense.Utils;
using Serilog;

namespace ESPresense.Optimizers;

public class IsotonicRegressionOptimizer : IOptimizer
{
    private readonly State _state;

    public IsotonicRegressionOptimizer(State state)
    {
        _state = state;
    }

    public string Name => "Isotonic Regression";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        OptimizationResults results = new();
        var optimization = _state.Config?.Optimization ?? throw new InvalidOperationException("Optimization config not found");

        foreach (var rxGroup in os.ByRx())
        {
            try
            {
                var proposed = OptimizeReceiver(rxGroup, optimization, existingSettings);
                if (proposed != null)
                {
                    results.Nodes[rxGroup.Key.Id] = proposed;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Isotonic regression optimizer failed for node {NodeId}", rxGroup.Key.Id);
            }
        }

        return results;
    }

    private ProposedValues? OptimizeReceiver(IGrouping<OptNode, Measure> rxGroup, ConfigOptimization optimization, Dictionary<string, NodeSettings> existingSettings)
    {
        var samples = BuildSamples(rxGroup, existingSettings);
        if (samples.Count < 4)
            return null;

        var ordered = samples.OrderBy(s => s.Distance).ToList();
        var distances = ordered.Select(s => s.Distance).ToArray();
        var logDistances = ordered.Select(s => s.LogDistance).ToArray();
        var targets = ordered.Select(s => s.Target).ToArray();
        var weights = ordered.Select(s => s.Weight).ToArray();

        var smoothedTargets = MathUtils.IsotonicRegression(distances, targets, weights, increasing: false);
        var fit = MathUtils.WeightedLinearRegression(logDistances, smoothedTargets, weights);
        if (fit == null)
            return null;

        var (slope, intercept) = fit.Value;
        if (double.IsNaN(slope) || double.IsNaN(intercept))
            return null;

        var absorption = Math.Clamp(-slope / 10.0, optimization.AbsorptionMin, optimization.AbsorptionMax);
        var rxAdj = Math.Clamp(intercept, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax);

        double rmse = CalculateWeightedRmse(ordered, absorption, rxAdj);

        return new ProposedValues
        {
            Absorption = absorption,
            RxAdjRssi = rxAdj,
            Error = double.IsNaN(rmse) ? null : rmse
        };
    }

    private static List<Sample> BuildSamples(IGrouping<OptNode, Measure> rxGroup, Dictionary<string, NodeSettings> existingSettings)
    {
        var samples = new List<Sample>();

        foreach (var measure in rxGroup)
        {
            if (measure.Rx?.Location == null || measure.Tx?.Location == null)
                continue;

            double distance = measure.Rx.Location.DistanceTo(measure.Tx.Location);
            if (distance <= 0 || double.IsNaN(distance) || double.IsInfinity(distance))
                continue;

            double logDistance = Math.Log10(distance);
            double txRef = ResolveTxRef(measure, existingSettings);
            double adjustedRssi = measure.GetAdjustedRssi(0); // Normalize all samples to an RxAdj baseline of 0
            double target = adjustedRssi - txRef;

            if (double.IsNaN(target) || double.IsInfinity(target))
                continue;

            double weight = CalculateWeight(measure);
            samples.Add(new Sample(distance, logDistance, target, weight));
        }

        return samples;
    }

    private static double CalculateWeight(Measure measure)
    {
        double weight = 1.0;
        if (measure.DistVar.HasValue)
            weight /= 1.0 + Math.Max(0, measure.DistVar.Value);
        if (measure.RssiVar.HasValue)
            weight /= 1.0 + Math.Max(0, measure.RssiVar.Value);
        return Math.Max(weight, 1e-3);
    }

    private static double ResolveTxRef(Measure measure, Dictionary<string, NodeSettings> existingSettings)
    {
        if (existingSettings.TryGetValue(measure.Tx.Id, out var txSettings))
        {
            var configured = txSettings?.Calibration?.TxRefRssi;
            if (configured.HasValue)
                return configured.Value;
        }

        return measure.RefRssi;
    }

    private static double CalculateWeightedRmse(IEnumerable<Sample> samples, double absorption, double rxAdj)
    {
        double weightedError = 0;
        double weightSum = 0;

        foreach (var sample in samples)
        {
            double predicted = rxAdj - 10.0 * absorption * sample.LogDistance;
            double residual = sample.Target - predicted;
            weightedError += sample.Weight * residual * residual;
            weightSum += sample.Weight;
        }

        return weightSum <= 0 ? double.NaN : Math.Sqrt(weightedError / weightSum);
    }

    private sealed record Sample(double Distance, double LogDistance, double Target, double Weight);
}
