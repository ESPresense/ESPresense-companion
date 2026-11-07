using System.Text;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;

namespace ESPresense.Optimizers;

/// <summary>
/// Fits each receiver's rxAdj and absorption on devices at known positions (taught spots), #1868. For a spot heard
/// by receivers j, each reading implies the device's level <c>raw_j − rxAdj_j + 10·absorption_j·log10 d_j</c>; the
/// fit minimises how much those disagree within each spot (the spot's level is free), which is exactly what the spot
/// score measures and what moves a trilaterated position.
///
/// A common shift of every rxAdj leaves that unchanged (each spot's level absorbs it), so the fit cannot place it:
/// the mean rxAdj over the receivers it fits is put back at the incumbent's, leaving the absolute level where the
/// anchors and node links put it. Only receivers heard in at least <see cref="MinSpotsPerReceiver"/> spots get a value.
///
/// It only sees the spots it is given. The runner gives it a fixed share of the taught spots and judges proposals on
/// the rest (<see cref="SplitForJudging"/>), so its proposal is never scored on the spots it was fitted to.
/// </summary>
internal sealed class SpotFitOptimizer(IReadOnlyList<KnownPositionSpot> spots, ConfigOptimization optimization) : IOptimizer
{
    internal const int MinSpots = 4;
    internal const int MinSpotsPerReceiver = 3;

    /// <summary>
    /// The largest change one cycle may make. A real correction keeps winning on the judge's spots and arrives over a
    /// few cycles; a large one-off value (often a receiver whose configured position is wrong, compensated locally)
    /// stops as soon as it stops helping. The whole step is scaled down, not clamped per receiver, so its mean stays put.
    /// </summary>
    internal const double MaxRxAdjStepDb = 3;
    internal const double MaxAbsorptionStep = 0.2;

    /// <summary>A 0.5 deviation of absorption from the middle of its limits costs as much as a 1 dB residual per spot.</summary>
    private const double AbsorptionPrior = 4;

    public string Name => "Spot Fit";

    public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
    {
        var results = new OptimizationResults();
        var heard = spots.SelectMany(s => s.Readings.Select(r => r.RxId)).GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() >= MinSpotsPerReceiver).Select(g => g.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var index = heard.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i, StringComparer.Ordinal);

        // Each spot's readings by receivers being fitted: (receiver index, raw, 10·log10 d).
        var data = spots
            .Select(s => s.Readings
                .Where(r => index.ContainsKey(r.RxId) && r.RxLocation.DistanceTo(s.Location) > 0)
                .Select(r => (I: index[r.RxId], Raw: r.RawRssi, L: 10 * Math.Log10(r.RxLocation.DistanceTo(s.Location))))
                .ToArray())
            .Where(s => s.Length >= SpotScore.MinReceivers)
            .ToArray();
        if (data.Length < MinSpots || heard.Count == 0) return results;

        int n = heard.Count;
        var mid = (optimization.AbsorptionMin + optimization.AbsorptionMax) / 2;
        var incumbent = heard.Select(id => (double)(existingSettings.TryGetValue(id, out var ns) ? ns.Calibration.RxAdjRssi ?? 0 : 0)).ToArray();

        // x = [rxAdj_0..n-1, absorption_0..n-1]
        double Objective(Vector<double> x)
        {
            double sse = 0;
            foreach (var s in data)
            {
                double mean = 0;
                foreach (var (i, raw, l) in s) mean += raw - x[i] + x[n + i] * l;
                mean /= s.Length;
                foreach (var (i, raw, l) in s) { var r = raw - x[i] + x[n + i] * l - mean; sse += r * r; }
            }
            double prior = 0;
            for (int i = 0; i < n; i++) prior += (x[n + i] - mid) * (x[n + i] - mid);
            return sse / data.Length + AbsorptionPrior / n * prior;
        }

        Vector<double> Gradient(Vector<double> x)
        {
            var g = Vector<double>.Build.Dense(2 * n);
            foreach (var s in data)
            {
                double mean = 0;
                foreach (var (i, raw, l) in s) mean += raw - x[i] + x[n + i] * l;
                mean /= s.Length;
                // The centring drops out of the gradient: the residuals of a spot sum to zero.
                foreach (var (i, raw, l) in s)
                {
                    var r = raw - x[i] + x[n + i] * l - mean;
                    g[i] += -2 * r / data.Length;
                    g[n + i] += 2 * r * l / data.Length;
                }
            }
            for (int i = 0; i < n; i++) g[n + i] += 2 * AbsorptionPrior / n * (x[n + i] - mid);
            return g;
        }

        var lower = Vector<double>.Build.Dense(2 * n); var upper = Vector<double>.Build.Dense(2 * n); var x0 = Vector<double>.Build.Dense(2 * n);
        for (int i = 0; i < n; i++)
        {
            lower[i] = optimization.RxAdjRssiMin; upper[i] = optimization.RxAdjRssiMax;
            lower[n + i] = optimization.AbsorptionMin; upper[n + i] = optimization.AbsorptionMax;
            x0[i] = Math.Clamp(incumbent[i], lower[i], upper[i]);
            var a = existingSettings.TryGetValue(heard[i], out var ns) ? ns.Calibration.Absorption ?? mid : mid;
            x0[n + i] = Math.Clamp(a, lower[n + i], upper[n + i]);
        }

        Vector<double> x;
        try
        {
            x = new BfgsBMinimizer(1e-8, 1e-8, 1e-8, 10000)
                .FindMinimum(ObjectiveFunction.Gradient(Objective, Gradient), lower, upper, x0).MinimizingPoint;
        }
        catch (Exception)
        {
            return results;
        }

        // Put the common level back where it was (the spots cannot see it; see the class summary), then cap the step.
        var shift = incumbent.Average() - Enumerable.Range(0, n).Average(i => x[i]);
        var rxStep = Enumerable.Range(0, n).Select(i => x[i] + shift - incumbent[i]).ToArray();
        var absStep = Enumerable.Range(0, n).Select(i => x[n + i] - x0[n + i]).ToArray();
        double Scale(double[] step, double max) { var m = step.Max(Math.Abs); return m > max ? max / m : 1; }
        var rxScale = Scale(rxStep, MaxRxAdjStepDb); var absScale = Scale(absStep, MaxAbsorptionStep);
        for (int i = 0; i < n; i++)
        {
            results.Nodes[heard[i]] = new ProposedValues
            {
                RxAdjRssi = Math.Clamp(incumbent[i] + rxStep[i] * rxScale, optimization.RxAdjRssiMin, optimization.RxAdjRssiMax),
                Absorption = Math.Clamp(x0[n + i] + absStep[i] * absScale, optimization.AbsorptionMin, optimization.AbsorptionMax)
            };
        }
        return results;
    }

    /// <summary>
    /// Splits taught spots between fitting and judging by a stable hash of the spot id: a third are judged, the rest
    /// fitted. Stable across restarts (string.GetHashCode is not), so a spot stays on its side.
    /// </summary>
    internal static (List<KnownPositionSpot> Fit, List<KnownPositionSpot> Judge) SplitForJudging(IEnumerable<KnownPositionSpot> taught)
    {
        var fit = new List<KnownPositionSpot>(); var judge = new List<KnownPositionSpot>();
        foreach (var s in taught) (Fnv1a(s.Id) % 3 == 0 ? judge : fit).Add(s);
        return (fit, judge);
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 16777619; }
        return h;
    }
}
