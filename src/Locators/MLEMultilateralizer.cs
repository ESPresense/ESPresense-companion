using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Weighting;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class MLEMultilateralizer : BaseMultilateralizer
{
    private readonly IWeighting _weighting;

    public MLEMultilateralizer(Device device, Floor floor, State state, NodeSettingsStore nodeSettings, DeviceSettingsStore deviceSettings) : base(device, floor, state, nodeSettings, deviceSettings)
    {
        // Load weighting from MLE config, defaulting to Gaussian if not specified
        _weighting = WeightingFactory.Create(state.Config?.Locators?.Mle?.Weighting);
    }

    protected override Point3D? Solve(Scenario scenario, DeviceToNode[] nodes, Point3D guess)
    {
        double Error(IList<double> x, DeviceToNode dn)
        {
            double expectedDistance = Math.Sqrt(Math.Pow(dn.Node!.Location.X - x[0], 2) +
                                                Math.Pow(dn.Node!.Location.Y - x[1], 2) +
                                                Math.Pow(dn.Node!.Location.Z - x[2], 2));
            double error = dn.Distance - expectedDistance;
            double fallbackVariance = 1e-6; // This should be a small positive number
            double variance = dn.DistVar ?? fallbackVariance;
            variance = Math.Max(variance, fallbackVariance); // Ensure variance is never zero

            return error * error / (2 * variance);
        }

        var weights = nodes.Select((dn, i) => _weighting.Get(i, nodes.Length)).ToArray();
        var weightSum = weights.Sum();
        if (weightSum <= 0) weightSum = 1;

        Vector<double>? lowerBound = null;
        Vector<double>? upperBound = null;
        if (Floor.Bounds != null && Floor.Bounds.Length >= 2)
        {
            lowerBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[0].X, Floor.Bounds[0].Y, Floor.Bounds[0].Z, 0.5 });
            upperBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[1].X, Floor.Bounds[1].Y, Floor.Bounds[1].Z, 1.5 });
        }
        var obj = ObjectiveFunction.Value(
            x =>
            {
                var distanceFromBoundingBox = lowerBound == null || upperBound == null
                    ? 0
                    : lowerBound.Subtract(x)
                        .PointwiseMaximum(x.Subtract(upperBound))
                        .PointwiseMaximum(0)
                        .L2Norm();
                return (distanceFromBoundingBox > 0 ? Math.Pow(5, 1 + distanceFromBoundingBox) : 0) + Math.Pow(5 * (1 - x[3]), 2) + nodes
                    .Select((dn, i) => new { err = Error(x, dn), weight = weights[i] })
                    .Sum(a => a.weight * a.err) / weightSum;
            });

        var clampedGuess = ClampToFloorBounds(guess);
        var initialGuess = Vector<double>.Build.DenseOfArray(new[]
        {
            clampedGuess.X,
            clampedGuess.Y,
            clampedGuess.Z,
            scenario.Scale ?? 1.0
        });
        double xDelta = Floor.Bounds == null ? 0.1 : Math.Max((Floor.Bounds[1].X - Floor.Bounds[0].X) * 0.05, 0.1);
        double yDelta = Floor.Bounds == null ? 0.1 : Math.Max((Floor.Bounds[1].Y - Floor.Bounds[0].Y) * 0.05, 0.1);
        double zDelta = Floor.Bounds == null ? 0.1 : Math.Max((Floor.Bounds[1].Z - Floor.Bounds[0].Z) * 0.05, 0.1);
        double scaleDelta = Math.Max(Math.Abs(initialGuess[3]) * 0.05, 0.05);
        var initialPerturbation = Vector<double>.Build.DenseOfArray([xDelta, yDelta, zDelta, scaleDelta]);
        var solver = new NelderMeadSimplex(1e-7, 10000);

        try
        {
            var result = solver.FindMinimum(obj, initialGuess, initialPerturbation);
            var minimizingPoint = lowerBound == null || upperBound == null
                ? result.MinimizingPoint
                : result.MinimizingPoint.PointwiseMinimum(upperBound).PointwiseMaximum(lowerBound);
            scenario.Scale = minimizingPoint[3];
            scenario.Fixes = nodes.Length;
            scenario.Error = result.FunctionInfoAtMinimum.Value;
            scenario.Iterations = result switch
            {
                MinimizationWithLineSearchResult mwl =>
                    mwl.IterationsWithNonTrivialLineSearch + mwl.TotalLineSearchIterations,
                _ => result.Iterations
            };
            scenario.ReasonForExit = result.ReasonForExit;
            return new Point3D(minimizingPoint[0], minimizingPoint[1], minimizingPoint[2]);
        }
        catch (MaximumIterationsException)
        {
            scenario.ReasonForExit = ExitCondition.ExceedIterations;
            return null; // Base template will fall back to using guess with confidence=1
        }
    }
}
