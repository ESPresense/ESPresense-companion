using System.Numerics;
using ESPresense.Utils;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class GaussNewtonMultilateralizer : BaseMultilateralizer
{
    public GaussNewtonMultilateralizer(Device device, Floor floor, State state)
        : base(device, floor, state)
    {
    }

    public override bool Locate(Scenario scenario)
    {
        double Error(Point3D pos1, Point3D pos2, double dist) => pos1.DistanceTo(pos2) - dist;

        if (!InitializeScenario(scenario, out var nodes, out var guess))
            return false;

        var (pos, ranges) = SelectNonColinearTransmitters(nodes, 4);

        if (pos.Length <= 1)
        {
            ResetScenario(scenario);
            return false;
        }

        scenario.Minimum = ranges.Min(a => a);
        scenario.Fixes = pos.Length;

        int confidence = scenario.Confidence ?? 0;
        try
        {
            if (pos.Length < 3 || Floor.Bounds == null || Floor.Bounds.Length < 2)
            {
                confidence = 1;
                scenario.UpdateLocation(guess);
            }
            else
            {
                var lowerBound = new Vector3((float)Floor.Bounds[0].X, (float)Floor.Bounds[0].Y, (float)Floor.Bounds[0].Z);
                var upperBound = new Vector3((float)Floor.Bounds[1].X, (float)Floor.Bounds[1].Y, (float)Floor.Bounds[1].Z);
                var clampedGuess = ClampToFloorBounds(guess);
                var initialGuess = new Vector3((float)clampedGuess.X, (float)clampedGuess.Y, (float)clampedGuess.Z);

                var gaussNewton = new GaussNewton(pos, ranges, lowerBound, upperBound);
                var result = gaussNewton.FindPosition(initialGuess);

                scenario.UpdateLocation(new Point3D(result.X, result.Y, result.Z));
                scenario.Fixes = pos.Length;
                scenario.Error = pos.Select((p, i) => Math.Pow(Error(result.ToPoint3D(), p.ToPoint3D(), ranges[i]), 2)).Average();
                scenario.Iterations = gaussNewton.Iterations;

                scenario.ReasonForExit = ExitCondition.Converged;
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

        return FinalizeScenario(scenario, confidence);
    }

    private Tuple<Vector3[], float[]> SelectNonColinearTransmitters(DeviceToNode[] dns, int numberOfTransmitters = 4)
    {
        var orderedTransmitters = dns.OrderBy(a => a.Distance).ToArray();

        var selectedTransmitters = new List<Vector3>();
        var selectedRanges = new List<float>();

        for (var i = 0; i < orderedTransmitters.Length; ++i)
        {
            if (selectedTransmitters.Count < 1)
            {
                selectedTransmitters.Add(orderedTransmitters[i].Node!.Location.ToVector3());
                selectedRanges.Add((float)orderedTransmitters[i].Distance);
            }
            else
            {
                var isColinear = false;
                for (var j = 0; j < selectedTransmitters.Count - 1; ++j)
                {
                    for (var k = j + 1; k < selectedTransmitters.Count; ++k)
                    {
                        var v1 = selectedTransmitters[j] - orderedTransmitters[i].Node!.Location.ToVector3();
                        var v2 = selectedTransmitters[k] - orderedTransmitters[i].Node!.Location.ToVector3();
                        if (Math.Abs(Vector3.Cross(v1, v2).Length()) < 1e-5)
                        {
                            isColinear = true;
                            break;
                        }
                    }

                    if (isColinear) break;
                }

                if (!isColinear)
                {
                    selectedTransmitters.Add(orderedTransmitters[i].Node!.Location.ToVector3());
                    selectedRanges.Add((float)orderedTransmitters[i].Distance);
                }
            }

            if (selectedTransmitters.Count >= numberOfTransmitters) break;
        }

        if (selectedTransmitters.Count < numberOfTransmitters)
            return new Tuple<Vector3[], float[]>(dns.Select(a => a.Node!.Location.ToVector3()).ToArray(), dns.Select(a => (float)a.Distance).ToArray());

        return new Tuple<Vector3[], float[]>(selectedTransmitters.ToArray(), selectedRanges.ToArray());
    }

    private class GaussNewton
    {
        private readonly Vector3 _lowerBounds;
        private readonly float[] _ranges;
        private readonly Vector3[] _transmitters;
        private readonly Vector3 _upperBounds;

        public GaussNewton(Vector3[] transmitters, float[] ranges, Vector3 lowerBounds, Vector3 upperBounds)
        {
            _transmitters = transmitters;
            _ranges = ranges;
            _lowerBounds = lowerBounds;
            _upperBounds = upperBounds;
        }

        public int? Iterations { get; set; }

        private double Error(Vector3 x, Vector3 dnLocation, float dnDistance)
        {
            return Vector3.Distance(x, dnLocation) - dnDistance;
        }

        public Vector3 FindPosition(Vector3 initialGuess)
        {
            var guess = initialGuess;
            var lambda = 1e-3; // Regularization parameter

            for (var iter = 0; iter < 100; ++iter)
            {
                // Construct the Jacobian matrix
                var jacobian = DenseMatrix.OfArray(new double[_transmitters.Length, 3]);
                for (var i = 0; i < _transmitters.Length; ++i)
                    for (var j = 0; j < 3; ++j)
                        jacobian[i, j] = 2 * (guess - _transmitters[i])[j];

                // Construct the residual vector
                var residuals = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(_transmitters.Length);
                for (var i = 0; i < _transmitters.Length; ++i) residuals[i] = Error(guess, _transmitters[i], _ranges[i]);

                // Construct the normal equations with regularization
                var normalMatrix = jacobian.TransposeThisAndMultiply(jacobian);
                normalMatrix += DenseMatrix.CreateIdentity(3) * lambda;
                var step = normalMatrix.Inverse() * jacobian.Transpose() * residuals;

                // Update the guess
                guess -= new Vector3((float)step[0], (float)step[1], (float)step[2]);

                guess = Vector3.Max(_lowerBounds, Vector3.Min(_upperBounds, guess));

                // If the step size is small enough, stop iterating
                if (step.L2Norm() < 1e-5) break;
            }

            return guess;
        }
    }
}