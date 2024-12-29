using ESPresense.Locators;
using MathNet.Numerics.Optimization;
using MathNet.Spatial.Euclidean;
using Newtonsoft.Json;
using MathNet.Numerics.LinearAlgebra;

namespace ESPresense.Models;

public class Scenario(Config? config, ILocate locator, string? name)
{
    private Config? Config { get; } = config;
    private ILocate Locator { get; } = locator;

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Config?.Timeout ?? 30);
    public int? Confidence { get; set; }
    public double? Minimum { get; set; }
    [JsonIgnore] public Point3D LastLocation { get; set; }
    public Point3D Location { private set; get; }
    public double? Scale { get; set; }
    public int? Fixes { get; set; }
    public string? Name { get; } = name;
    public Room? Room { get; set; }
    public double? Error { get; set; }
    public int? Iterations { get; set; }
    public ExitCondition ReasonForExit { get; set; }
    public Floor? Floor { get; set; }
    public DateTime? LastHit { get; set; }
    public double Probability { get; set; } = 1.0;

    // Kalman filter properties
    private Matrix<double>? _kalmanStateEstimate;
    private Matrix<double>? _kalmanErrorCovariance;
    private const double ProcessNoise = 0.01;
    private const double MeasurementNoise = 0.1;
    private Matrix<double>? _F; // State transition matrix
    private Matrix<double>? _H; // Measurement matrix
    private Matrix<double>? _Q; // Process noise covariance
    private Matrix<double>? _R; // Measurement noise covariance

    public bool Locate()
    {
        return Locator.Locate(this);
    }

    public void UpdateLocation(Point3D newLocation)
    {
        if (_kalmanStateEstimate == null || _kalmanErrorCovariance == null)
        {
            InitializeKalmanFilter(newLocation);
        }

        var dt = (DateTime.UtcNow - (LastHit ?? DateTime.UtcNow)).TotalSeconds;
        LastHit = DateTime.UtcNow;

        UpdateStateTransitionMatrix(dt);

        // Predict
        if (_F == null || _kalmanStateEstimate == null || _kalmanErrorCovariance == null || _H == null || _Q == null || _R == null)
            return;

        _kalmanStateEstimate = _F * _kalmanStateEstimate;
        _kalmanErrorCovariance = _F * _kalmanErrorCovariance * _F.Transpose() + _Q;

        // Update
        var y = Matrix<double>.Build.DenseOfArray(new double[,] {
            { newLocation.X }, { newLocation.Y }, { newLocation.Z }
        }) - _H * _kalmanStateEstimate;

        var S = _H * _kalmanErrorCovariance * _H.Transpose() + _R;
        var K = _kalmanErrorCovariance * _H.Transpose() * S.Inverse();

        _kalmanStateEstimate += K * y;
        _kalmanErrorCovariance = (Matrix<double>.Build.DenseIdentity(6) - K * _H) * _kalmanErrorCovariance;

        // Update Location
        Location = new Point3D(
            _kalmanStateEstimate[0, 0],
            _kalmanStateEstimate[1, 0],
            _kalmanStateEstimate[2, 0]
        );
    }

    private void InitializeKalmanFilter(Point3D initialLocation)
    {
        _kalmanStateEstimate = Matrix<double>.Build.DenseOfArray(new double[,] {
            { initialLocation.X }, { initialLocation.Y }, { initialLocation.Z },
            { 0 }, { 0 }, { 0 }
        });
        _kalmanErrorCovariance = Matrix<double>.Build.DenseDiagonal(6, 6, 1);

        _H = Matrix<double>.Build.DenseOfArray(new double[,] {
            { 1, 0, 0, 0, 0, 0 },
            { 0, 1, 0, 0, 0, 0 },
            { 0, 0, 1, 0, 0, 0 }
        });

        _Q = Matrix<double>.Build.DenseDiagonal(6, 6, ProcessNoise);
        _R = Matrix<double>.Build.DenseDiagonal(3, 3, MeasurementNoise);
    }

    private void UpdateStateTransitionMatrix(double dt)
    {
        _F = Matrix<double>.Build.DenseOfArray(new double[,] {
            { 1, 0, 0, dt, 0, 0 },
            { 0, 1, 0, 0, dt, 0 },
            { 0, 0, 1, 0, 0, dt },
            { 0, 0, 0, 1, 0, 0 },
            { 0, 0, 0, 0, 1, 0 },
            { 0, 0, 0, 0, 0, 1 }
        });
    }
}
