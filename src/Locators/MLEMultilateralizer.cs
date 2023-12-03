using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class MLEMultilateralizer(Device device, Floor floor, State state) : ILocate
{
    public bool Locate(Scenario scenario)
    {
        double Error(IList<double> x, DeviceNode dn)
        {
            double expectedDistance = Math.Sqrt(Math.Pow(dn.Node!.Location.X - x[0], 2) +
                                                Math.Pow(dn.Node!.Location.Y - x[1], 2) +
                                                Math.Pow(dn.Node!.Location.Z - x[2], 2));
            double error = dn.Distance - expectedDistance;
            double fallbackVariance = 1e-6; // This should be a small positive number
            double variance = dn.Variance ?? fallbackVariance;
            variance = Math.Max(variance, fallbackVariance); // Ensure variance is never zero

            return error * error / (2 * variance);
        }

        var confidence = scenario.Confidence;

        var nodes = device.Nodes.Values.Where(a => a.Current && (a.Node?.Floors?.Contains(floor) ?? false)).OrderBy(a => a.Distance).ToArray();
        var pos = nodes.Select(a => a.Node!.Location).ToArray();

        scenario.Minimum = nodes.Min(a => (double?)a.Distance);
        scenario.LastHit = nodes.Max(a => a.LastHit);
        scenario.Fixes = pos.Length;

        if (pos.Length <= 1)
        {
            scenario.Room = null;
            scenario.Confidence = 0;
            scenario.Error = null;
            scenario.Floor = null;
            return false;
        }

        scenario.Floor = floor;

        var guess = confidence < 5
            ? Point3D.MidPoint(pos[0], pos[1])
            : scenario.Location;
        try
        {
            if (pos.Length < 3 || floor.Bounds == null)
            {
                confidence = 1;
                scenario.Location = guess;
            }
            else
            {
                var lowerBound = Vector<double>.Build.DenseOfArray(new[] { floor.Bounds[0].X, floor.Bounds[0].Y, floor.Bounds[0].Z, 0.5 });
                var upperBound = Vector<double>.Build.DenseOfArray(new[] { floor.Bounds[1].X, floor.Bounds[1].Y, floor.Bounds[1].Z, 1.5 });
                var obj = ObjectiveFunction.Value(
                    x =>
                    {
                        var distanceFromBoundingBox = lowerBound.Subtract(x)
                            .PointwiseMaximum(x.Subtract(upperBound))
                            .PointwiseMaximum(0)
                            .L2Norm();
                        return (distanceFromBoundingBox > 0 ? Math.Pow(5, 1 + distanceFromBoundingBox) : 0) + Math.Pow(5 * (1 - x[3]), 2) + nodes
                            .Select((dn, i) => new { err = Error(x, dn), weight = state?.Weighting?.Get(i, nodes.Length) ?? 1.0 })
                            .Average(a => a.weight * Math.Pow(a.err, 2));
                    });

                var initialGuess = Vector<double>.Build.DenseOfArray(new[]
                {
                    Math.Max(floor.Bounds[0].X, Math.Min(floor.Bounds[1].X, guess.X)),
                    Math.Max(floor.Bounds[0].Y, Math.Min(floor.Bounds[1].Y, guess.Y)),
                    Math.Max(floor.Bounds[0].Z, Math.Min(floor.Bounds[1].Z, guess.Z)),
                    scenario.Scale ?? 1.0
                });
                var centroid = Point3D.Centroid(nodes.Select(n => n.Node!.Location).Take(3)).ToVector();
                var vectorToCentroid = centroid.Subtract(initialGuess.SubVector(0, 3)).Normalize(2);
                var scaleDelta = 0.05 * initialGuess[3];
                var initialPerturbation = Vector<double>.Build.DenseOfEnumerable(vectorToCentroid.Append(scaleDelta));
                var solver = new NelderMeadSimplex(1e-7, 10000);
                var result = solver.FindMinimum(obj, initialGuess, initialPerturbation);
                var minimizingPoint = result.MinimizingPoint.PointwiseMinimum(upperBound).PointwiseMaximum(lowerBound);
                scenario.Location = new Point3D(minimizingPoint[0], minimizingPoint[1], minimizingPoint[2]);
                scenario.Scale = minimizingPoint[3];
                scenario.Fixes = pos.Length;
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
            scenario.Location = guess;
        }
        catch (Exception ex)
        {
            confidence = 0;
            scenario.Location = new Point3D();
            Log.Error("Error finding location for {0}: {1}", device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;
        scenario.Room = floor.Rooms.Values.FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        return true;
    }
}