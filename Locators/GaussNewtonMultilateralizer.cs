using System.Numerics;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class GaussNewtonMultilateralizer : ILocate
{
    class GaussNewton
{
    private Vector3[] transmitters;
    private float[] ranges;
    private Vector3 lowerBounds;
    private Vector3 upperBounds;

    public GaussNewton(Vector3[] transmitters, float[] ranges, Vector3 lowerBounds, Vector3 upperBounds)
    {
        this.transmitters = transmitters;
        this.ranges = ranges;
        this.lowerBounds = lowerBounds;
        this.upperBounds = upperBounds;
    }

        public int? Iterations { get; set; }
    private bool OutOfBounds(Vector3 x)
    {
        return x.X < lowerBounds.X || x.X > upperBounds.X || x.Y < lowerBounds.Y || x.Y > upperBounds.Y || x.Z < lowerBounds.Z || x.Z > upperBounds.Z;
    }

    private double Error(Vector3 x, Vector3 dnLocation, float dnDistance)
    {
        return Vector3.Distance(x, dnLocation) - dnDistance;
    }
    public Vector3 FindPosition(Vector3 initialGuess)
    {
        var guess = initialGuess;

            int iter;
        for (iter = 0; iter < 100; ++iter)
        {
            // Construct the Jacobian matrix
            var jacobian = DenseMatrix.OfArray(new double[transmitters.Length, 3]);
            for (int i = 0; i < transmitters.Length; ++i)
            {
                for (int j = 0; j < 3; ++j)
                {
                    jacobian[i, j] = 2 * (guess - transmitters[i])[j];
                }
            }

            // Construct the residual vector
            var residuals = MathNet.Numerics.LinearAlgebra.Vector<double>.Build.Dense(transmitters.Length);
            for (int i = 0; i < transmitters.Length; ++i)
            {
                residuals[i] = Error(guess, transmitters[i], ranges[i]);
            }

            // Solve the normal equations
            var step = (jacobian.TransposeThisAndMultiply(jacobian)).Inverse() * jacobian.Transpose() * residuals;

            // Update the guess
            guess -= new Vector3((float)step[0], (float)step[1], (float)step[2]);

            // If the step size is small enough, stop iterating
            if (step.L2Norm() < 1e-5)
            {
                break;
            }
        }

            Iterations = iter;
        return guess;
    }
}


    private readonly Device _device;
    private readonly Floor _floor;
    private readonly State _state;

    public GaussNewtonMultilateralizer(Device device, Floor floor, State state)
    {
        _device = device;
        _floor = floor;
        _state = state;
    }

    public bool Locate(Scenario scenario)
    {
        var confidence = scenario.Confidence;

        var nodes = _device.Nodes.Values.Where(a => a.Current && (a.Node?.Floors?.Contains(_floor) ?? false)).OrderBy(a => a.Distance).ToArray();
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

        scenario.Floor = _floor;

        var guess = confidence < 5
            ? Point3D.MidPoint(pos[0], pos[1])
            : scenario.Location;
        try
        {
            // ...

            if (pos.Length < 3 || _floor.Bounds == null)
            {
                confidence = 1;
                scenario.Location = guess;
            }
            else
            {
                var lowerBound = new Vector3((float)_floor.Bounds[0].X, (float)_floor.Bounds[0].Y, (float)_floor.Bounds[0].Z);
                var upperBound = new Vector3((float)_floor.Bounds[1].X, (float)_floor.Bounds[1].Y, (float)_floor.Bounds[1].Z);
                var initialGuess = new Vector3(
                    Math.Max((float)_floor.Bounds[0].X, Math.Min((float)_floor.Bounds[1].X, (float)guess.X)),
                    Math.Max((float)_floor.Bounds[0].Y, Math.Min((float)_floor.Bounds[1].Y, (float)guess.Y)),
                    Math.Max((float)_floor.Bounds[0].Z, Math.Min((float)_floor.Bounds[1].Z, (float)guess.Z)));

                var multilateration = new GaussNewton(pos.Select(a => a.ToVector3()).ToArray(), nodes.Select(dn => (float)dn.Distance).ToArray(), lowerBound, upperBound);
                var result = multilateration.FindPosition(initialGuess);

                scenario.Location = new Point3D(result.X, result.Y, result.Z);
                scenario.Fixes = pos.Length;
                //scenario.Error = nodes.Select(dn => Math.Pow(multilateration.Error(result, dn.Node!.Location.ToVector3(), dn.Distance), 2)).Average();
                scenario.Scale = 1.0; // Gauss-Newton method doesn't estimate scale, so we'll just set it to 1.0
                scenario.Iterations = multilateration.Iterations;

                // Gauss-Newton method doesn't provide a reason for exit, so we'll just say it converged
                scenario.ReasonForExit = ExitCondition.Converged;

                confidence = (int)Math.Min(100, Math.Max(10, 100.0 - Math.Pow(scenario.Minimum ?? 1, 2)));
            }

            // ...

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
            Log.Error("Error finding location for {0}: {1}", _device, ex.Message);
        }

        scenario.Confidence = confidence;

        if (confidence <= 0) return false;
        if (Math.Abs(scenario.Location.DistanceTo(scenario.LastLocation)) < 0.1) return false;
        scenario.Room = _floor.Rooms.Values.FirstOrDefault(a => a.Polygon?.EnclosesPoint(scenario.Location.ToPoint2D()) ?? false);
        return true;
    }
}