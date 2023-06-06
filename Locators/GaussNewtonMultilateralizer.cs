using System.Numerics;
using ESPresense.Extensions;
using ESPresense.Models;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Serilog;

namespace ESPresense.Locators;

public class GaussNewtonMultilateralizer : ILocate
{
    public class Multilateration
    {
        private const double Epsilon = 0.001;
        private const int MaxIterations = 100;

        // Locations of the transmitters
        private readonly Vector3[] _transmitters;

        // Distances from the transmitters
        private readonly double[] ranges;
        private readonly Vector3 _lowerBounds;
        private readonly Vector3 _upperBounds;

        public Multilateration(Vector3[] transmitters, double[] ranges, Vector3 lowerBounds, Vector3 upperBounds)
        {
            this._transmitters = transmitters;
            this.ranges = ranges;
            _lowerBounds = lowerBounds;
            _upperBounds = upperBounds;
        }

        public int? Iterations { get; set; }

        public Vector3 FindPosition(Vector3 initialGuess)
        {
            Vector3 x = initialGuess;

            int k = 0;
            for (; k < MaxIterations; k++)
            {
                var J = new Matrix4x4();
                var r = new Vector4();

                for (int i = 0; i < _transmitters.Length; i++)
                {
                    var diff = x - _transmitters[i];
                    float rangeEstimate = diff.Length();

                    J[i, 0] = diff.X / rangeEstimate;
                    J[i, 1] = diff.Y / rangeEstimate;
                    J[i, 2] = diff.Z / rangeEstimate;

                    r[i] = (float)(rangeEstimate - ranges[i]);
                }

                var jt = Matrix4x4.Transpose(J);
                var jtj = Matrix4x4.Multiply(jt, J);
                var invJtj = jtj.Inverse();
                var delta = Vector4.Transform(-r, invJtj * jt);

                x += new Vector3(delta.X, delta.Y, delta.Z);

                x = Vector3.Max(_lowerBounds, Vector3.Min(_upperBounds, x));


                // Stopping criterion
                if (delta.Length() < Epsilon)
                    break;
            }

            Iterations = k;

            return x;
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

                var multilateration = new Multilateration(pos.Select(a => a.ToVector3()).ToArray(), nodes.Select(dn => dn.Distance).ToArray(), lowerBound, upperBound);
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