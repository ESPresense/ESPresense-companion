using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Weighting;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class BfgsMultilateralizer : BaseMultilateralizer
{
    private readonly IWeighting _weighting;

    public BfgsMultilateralizer(Device device, Floor floor, State state)
        : base(device, floor, state)
    {
        // Load weighting from BFGS config, defaulting to Gaussian if not specified
        _weighting = WeightingFactory.Create(state.Config?.Locators?.Bfgs?.Weighting);
    }

    public override bool Locate(Scenario scenario)
    {
        double Weight(int index, int total) => _weighting.Get(index, total);
        double Error(IList<double> x, DeviceToNode dn) => new Point3D(x[0], x[1], x[2]).DistanceTo(dn.Node!.Location) - dn.Distance;

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
                double weightSum = Enumerable.Range(0, nodes.Length)
                    .Select(i => Weight(i, nodes.Length))
                    .Sum();
                if (weightSum <= 0) weightSum = 1;

                var lowerBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[0].X, Floor.Bounds[0].Y, Floor.Bounds[0].Z });
                var upperBound = Vector<double>.Build.DenseOfArray(new[] { Floor.Bounds[1].X, Floor.Bounds[1].Y, Floor.Bounds[1].Z });
                var obj = ObjectiveFunction.Gradient(
                    x => nodes
                        .Select((dn, i) => new { err = Error(x, dn), weight = Weight(i, nodes.Length) })
                        .Sum(a => a.weight * Math.Pow(a.err, 2)) / weightSum
                    ,
                    x =>
                    {
                        var current = new Point3D(x[0], x[1], x[2]);
                        var gradient = new double[3];

                        for (var i = 0; i < 3; i++)
                        {
                            gradient[i] = nodes.Select((dn, j) =>
                            {
                                var nodeLoc = dn.Node!.Location;
                                var dist = current.DistanceTo(nodeLoc);
                                if (dist < 1e-6) dist = 1e-6; // Avoid division by zero

                                var err = dist - dn.Distance;
                                var dDist_dX = (x[i] - nodeLoc.ToVector()[i]) / dist;

                                return 2 * err * dDist_dX * Weight(j, nodes.Length);
                            }).Sum() / weightSum;
                        }
                        return Vector<double>.Build.Dense(gradient);
                    });

                var clampedGuess = ClampToFloorBounds(guess);
                var initialGuess = Vector<double>.Build.DenseOfArray(new[]
                {
                    clampedGuess.X,
                    clampedGuess.Y,
                    clampedGuess.Z
                });
                var solver = new BfgsBMinimizer(1e-5, 1e-5, 1e-5, 1000);
                var result = solver.FindMinimum(obj, lowerBound, upperBound, initialGuess);
                scenario.UpdateLocation(new Point3D(result.MinimizingPoint[0], result.MinimizingPoint[1], result.MinimizingPoint[2]));
                scenario.Fixes = nodes.Length;
                scenario.Error = result.FunctionInfoAtMinimum.Value;
                scenario.Iterations = result switch
                {
                    MinimizationWithLineSearchResult mwl =>
                        mwl.IterationsWithNonTrivialLineSearch + mwl.TotalLineSearchIterations,
                    _ => result.Iterations
                };

                scenario.ReasonForExit = result.ReasonForExit;

                CalculateAndSetPearsonCorrelation(scenario, nodes);

                // Calculate number of possible nodes for this floor
                int nodesPossibleOnline = State.Nodes.Values
                    .Count(n => n.Floors?.Contains(Floor) ?? false);

                // Use the centralized confidence calculation
                confidence = MathUtils.CalculateConfidence(
                    scenario.Error,
                    scenario.PearsonCorrelation,
                    nodes.Length,
                    nodesPossibleOnline
                );
            }
        }
        catch (Exception ex)
        {
            confidence = HandleLocatorException(ex, scenario, guess);
        }

        return FinalizeScenario(scenario, confidence);
    }
}
