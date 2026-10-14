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

        private double Cost(Vector3 x)
        {
            double sum = 0;
            for (var i = 0; i < _transmitters.Length; ++i)
            {
                var e = Error(x, _transmitters[i], _ranges[i]);
                sum += e * e;
            }
            return sum;
        }

        public Vector3 FindPosition(Vector3 initialGuess)
        {
            var guess = initialGuess;
            var lambda = 1e-3; // Levenberg damping; raised when a step fails, lowered when it helps
            var cost = Cost(guess);

            for (var iter = 0; iter < 100; ++iter)
            {
                // Construct the Jacobian matrix
                // The residual below is (|x - p| - d), so its gradient is the unit vector from the
                // transmitter towards the guess. Using 2*(x - p) instead -- the gradient of the
                // SQUARED-distance residual -- silently weights each node by its own range, which
                // drags the fix towards whichever node reads farthest.
                var jacobian = DenseMatrix.OfArray(new double[_transmitters.Length, 3]);
                for (var i = 0; i < _transmitters.Length; ++i)
                {
                    var delta = guess - _transmitters[i];
                    var norm = delta.Length();
                    for (var j = 0; j < 3; ++j)
                        jacobian[i, j] = norm > 1e-6 ? delta[j] / norm : 0.0;
                }

                // Construct the residual vector
                var residuals = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(_transmitters.Length);
                for (var i = 0; i < _transmitters.Length; ++i) residuals[i] = Error(guess, _transmitters[i], _ranges[i]);

                // Construct the normal equations with regularization
                var normalMatrix = jacobian.TransposeThisAndMultiply(jacobian);
                var gradient = jacobian.Transpose() * residuals;

                // Ranges that cannot all be satisfied at once -- which is every real radio fix --
                // make an undamped step overshoot and oscillate off the floor bounds. Only take a
                // step that actually lowers the cost; otherwise damp harder and try again.
                var accepted = false;
                for (var attempt = 0; attempt < 12; ++attempt)
                {
                    var damped = normalMatrix + DenseMatrix.CreateIdentity(3) * lambda;
                    var step = damped.Inverse() * gradient;

                    var candidate = guess - new Vector3((float)step[0], (float)step[1], (float)step[2]);
                    candidate = Vector3.Max(_lowerBounds, Vector3.Min(_upperBounds, candidate));

                    var candidateCost = Cost(candidate);
                    if (candidateCost <= cost)
                    {
                        var converged = (candidate - guess).Length() < 1e-5;
                        guess = candidate;
                        cost = candidateCost;
                        lambda = Math.Max(lambda / 3, 1e-9);
                        accepted = true;
                        if (converged) return guess;
                        break;
                    }

                    lambda *= 8;
                }

                // Damping can no longer buy an improvement: this is the minimum we can reach.
                if (!accepted) break;
            }

            return guess;
        }
    }
}