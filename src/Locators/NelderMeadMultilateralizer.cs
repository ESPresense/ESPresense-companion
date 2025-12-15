using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class NelderMeadMultilateralizer(Device device, Floor floor, State state) : BaseMultilateralizer(device, floor, state)
{
    public override bool Locate(Scenario scenario)
    {
        double Error(IList<double> x, DeviceToNode dn) => new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) * x[3] - dn.Distance;

        if (!InitializeScenario(scenario, out var nodes, out var guess))
            return false;

        int confidence = scenario.Confidence ?? 0;
        try
        {
            if (nodes.Length < 3 || Floor.Bounds == null || Floor.Bounds.Length < 2)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {
                var lowerBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[0].X, Floor.Bounds[0].Y, Floor.Bounds[0].Z, 0.5 });
                var upperBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[1].X, Floor.Bounds[1].Y, Floor.Bounds[1].Z, 1.5 });
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        var distanceFromBoundingBox = lowerBound.Subtract(x)
                            .PointwiseMaximum(x.Subtract(upperBound))
                            .PointwiseMaximum(0)
                            .L2Norm();
                        return (distanceFromBoundingBox > 0 ? Math.Pow(5, 1 + distanceFromBoundingBox) : 0) + Math.Pow(5 * (1 - x[3]), 2) + nodes
                            .Select((dn, i) => new { err = Error(x, dn), weight = State?.Weighting?.Get(i, nodes.Length) ?? 1.0 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                    });

                var clampedGuess = ClampToFloorBounds(guess);
                var initialGuess = Vector<double>.Build.DenseOfArray(new[]
                {
                    clampedGuess.X,
                    clampedGuess.Y,
                    clampedGuess.Z,
                    scenario.Scale ?? 1.0
                });
                var centroid = Point3D.Centroid(nodes.Select(n => n.Node!.Location).Take(3)).ToVector();
                var vectorToCentroid = centroid.Subtract(initialGuess.SubVector(0, 3)).Normalize(2);
                var scaleDelta = 0.05 * initialGuess[3];
                var initialPerturbation = Vector<double>.Build.DenseOfEnumerable(vectorToCentroid.Append(scaleDelta));
                var solver = new NelderMeadSimplex(1e-7, 10000);
                var result = solver.FindMinimum(obj, initialGuess, initialPerturbation);
                var minimizingPoint = result.MinimizingPoint.PointwiseMinimum(upperBound).PointwiseMaximum(lowerBound);
                scenario.UpdateLocation(new Point3D(minimizingPoint[0], minimizingPoint[1], minimizingPoint[2]));
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
                confidence = (int)Math.Min(100, Math.Max(10, 100.0 - (Math.Pow(scenario.Minimum ?? 1, 2) + Math.Pow(10 * (1 - (scenario.Scale ?? 1)), 2) + (scenario.Minimum + result.FunctionInfoAtMinimum.Value ?? 10.00))));
            }
        }
        catch (MaximumIterationsException)
        {
            scenario.ReasonForExit = ExitCondition.ExceedIterations;
            confidence = 1;
            scenario.UpdateLocation(guess);
        }
        catch (Exception ex)
        {
            confidence = HandleLocatorException(ex, scenario, guess);
        }

        CalculateAndSetPearsonCorrelation(scenario, nodes);

        return FinalizeScenario(scenario, confidence);
    }
}