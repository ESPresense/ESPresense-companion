using MathNet.Numerics.LinearAlgebra;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

/// <summary>
/// Implements Kalman filtering for 3D positions with velocity tracking
/// </summary>
public class KalmanLocation
{
    private Matrix<double>? _kalmanStateEstimate;
    private Matrix<double>? _kalmanErrorCovariance;
    private DateTime? _lastLocationUpdate;

    /// <summary>
    /// Settings for the Kalman filter (shared across devices)
    /// </summary>
    public KalmanFilterSettings Settings { get; set; }

    // The filtered location (x,y,z)
    public Point3D Location { get; private set; } = new Point3D();

    // The estimated velocity (vx,vy,vz)
    public Vector3D Velocity => _kalmanStateEstimate != null
        ? new Vector3D(_kalmanStateEstimate[3, 0], _kalmanStateEstimate[4, 0], _kalmanStateEstimate[5, 0])
        : new Vector3D(0, 0, 0);

    /// <summary>
    /// Gets the predicted location based on the current state without updating the filter
    /// </summary>
    /// <returns>The predicted location and predicted error covariance</returns>
    public (Point3D PredictedLocation, Matrix<double> PredictedCovariance) GetPrediction()
    {
        // If filter is not initialized, return current location and identity covariance
        if (_kalmanStateEstimate == null || _kalmanErrorCovariance == null)
        {
            return (Location, Matrix<double>.Build.DenseDiagonal(3, 3, 1.0));
        }

        // Calculate time delta
        var now = DateTime.UtcNow;
        var dt = _lastLocationUpdate.HasValue
            ? (now - _lastLocationUpdate.Value).TotalSeconds
            : 0.1; // Default to 100ms if no previous update

        // Create state transition matrix and process noise matrix
        var F = CreateStateTransitionMatrix(dt);
        var Q = CreateProcessNoiseMatrix(dt, Settings.ProcessNoise);

        // Predict step (without modifying internal state)
        var predictedState = F * _kalmanStateEstimate;
        var predictedCovariance = F * _kalmanErrorCovariance * F.Transpose() + Q;

        // Extract predicted location from state
        var predictedLocation = new Point3D(
            predictedState[0, 0],
            predictedState[1, 0],
            predictedState[2, 0]
        );

        return (predictedLocation, predictedCovariance);
    }

    /// <summary>
    /// Creates a new KalmanLocation with default settings
    /// </summary>
    public KalmanLocation() : this(KalmanFilterSettings.Default) { }

    /// <summary>
    /// Creates a new KalmanLocation with specified settings
    /// </summary>
    /// <param name="settings">The Kalman filter settings to use</param>
    public KalmanLocation(KalmanFilterSettings settings)
    {
        Settings = settings;
    }

    /// <summary>
    /// Updates the filtered location with a new measurement
    /// </summary>
    /// <param name="newLocation">The new measured location</param>
    /// <returns>The filtered location after applying the update</returns>
    public Point3D Update(Point3D newLocation)
    {
        return Update(newLocation, DateTime.UtcNow);
    }

    /// <summary>
    /// Updates the filtered location with a new measurement at a specific time
    /// </summary>
    /// <param name="newLocation">The new measured location</param>
    /// <param name="now">The timestamp of the measurement</param>
    /// <returns>The filtered location after applying the update</returns>
    public Point3D Update(Point3D newLocation, DateTime now)
    {
        // Initialize Kalman filter if this is the first update
        if (_kalmanStateEstimate == null || _kalmanErrorCovariance == null)
        {
            InitializeKalmanFilter(newLocation);
            Location = newLocation;
            _lastLocationUpdate = now;
            return Location;
        }

        // Calculate time delta
        var dt = _lastLocationUpdate.HasValue
            ? (now - _lastLocationUpdate.Value).TotalSeconds
            : 0.1; // Default to 100ms if no previous update
        _lastLocationUpdate = now;

        dt = Math.Max(dt, 0.001); // Minimum 1ms interval

        // Check if the proposed move exceeds human movement capabilities
        // Calculate the distance between the current and new location
        double distanceToNewLocation = Location.DistanceTo(newLocation);

        // Calculate the maximum possible distance a human could have moved in this timeframe
        double maxPossibleDistance = Settings.MaxVelocity * dt;

        // If the new location is too far away, adjust the measurement noise based on how extreme the movement is
        double dynamicMeasurementNoise = Settings.MeasurementNoise;
        if (distanceToNewLocation > maxPossibleDistance)
        {
            // Scale up the measurement noise based on how much the movement exceeds the maximum
            double excessFactor = distanceToNewLocation / maxPossibleDistance;
            dynamicMeasurementNoise *= excessFactor * excessFactor; // Square it for more aggressive rejection
        }

        // Update state transition matrix based on dt
        var F = CreateStateTransitionMatrix(dt);
        var Q = CreateProcessNoiseMatrix(dt, Settings.ProcessNoise);

        // Predict step
        _kalmanStateEstimate = F * _kalmanStateEstimate;
        _kalmanErrorCovariance = F * _kalmanErrorCovariance * F.Transpose() + Q;

        // Apply velocity constraints to the predicted state
        ConstrainVelocity();

        // Measurement matrix maps the state to the measurement
        var H = Matrix<double>.Build.DenseOfArray(new double[,] {
            { 1, 0, 0, 0, 0, 0 },
            { 0, 1, 0, 0, 0, 0 },
            { 0, 0, 1, 0, 0, 0 }
        });

        // Measurement noise covariance matrix - using our dynamic measurement noise
        var R = Matrix<double>.Build.DenseDiagonal(3, 3, dynamicMeasurementNoise);

        // Convert newLocation to measurement vector
        var measurement = Matrix<double>.Build.DenseOfArray(new double[,] {
            { newLocation.X }, { newLocation.Y }, { newLocation.Z }
        });

        // Calculate innovation (measurement residual)
        var predictedMeasurement = H * _kalmanStateEstimate;
        var innovation = measurement - predictedMeasurement;

        // Calculate innovation covariance
        var S = H * _kalmanErrorCovariance * H.Transpose() + R;

        // Calculate Kalman gain
        var K = _kalmanErrorCovariance * H.Transpose() * S.Inverse();

        // Update state estimate and error covariance
        _kalmanStateEstimate = _kalmanStateEstimate + K * innovation;
        _kalmanErrorCovariance = (Matrix<double>.Build.DenseIdentity(6) - K * H) * _kalmanErrorCovariance;

        // Apply velocity constraints again after the update
        ConstrainVelocity();

        // Update the filtered location
        Location = new Point3D(
            _kalmanStateEstimate[0, 0],
            _kalmanStateEstimate[1, 0],
            _kalmanStateEstimate[2, 0]
        );

        return Location;
    }

    /// <summary>
    /// Constrains the velocity in the state estimate to be within human movement capabilities
    /// </summary>
    private void ConstrainVelocity()
    {
        if (_kalmanStateEstimate == null) return;

        // Extract velocity components from state
        double vx = _kalmanStateEstimate[3, 0];
        double vy = _kalmanStateEstimate[4, 0];
        double vz = _kalmanStateEstimate[5, 0];

        // Calculate velocity magnitude
        double velocityMagnitude = Math.Sqrt(vx*vx + vy*vy + vz*vz);

        // If velocity exceeds maximum, scale it back while preserving direction
        if (velocityMagnitude > Settings.MaxVelocity && velocityMagnitude > 0)
        {
            double scaleFactor = Settings.MaxVelocity / velocityMagnitude;

            // Scale each component
            _kalmanStateEstimate[3, 0] = vx * scaleFactor;
            _kalmanStateEstimate[4, 0] = vy * scaleFactor;
            _kalmanStateEstimate[5, 0] = vz * scaleFactor;
        }
    }

    /// <summary>
    /// Resets the filter with a new location
    /// </summary>
    /// <param name="location">The location to reset to</param>
    public void Reset(Point3D location)
    {
        InitializeKalmanFilter(location);
        Location = location;
    }

    private void InitializeKalmanFilter(Point3D initialLocation)
    {
        // State vector: [x, y, z, vx, vy, vz]
        _kalmanStateEstimate = Matrix<double>.Build.DenseOfArray(new double[,] {
            { initialLocation.X }, { initialLocation.Y }, { initialLocation.Z }, { 0 }, { 0 }, { 0 }
        });

        // Initial error covariance matrix
        _kalmanErrorCovariance = Matrix<double>.Build.DenseDiagonal(6, 6, 1.0);

        // Store initial update time
        _lastLocationUpdate = DateTime.UtcNow;
    }

    private Matrix<double> CreateStateTransitionMatrix(double dt)
    {
        // State transition matrix for constant velocity model
        return Matrix<double>.Build.DenseOfArray(new double[,] {
            { 1, 0, 0, dt, 0, 0 },
            { 0, 1, 0, 0, dt, 0 },
            { 0, 0, 1, 0, 0, dt },
            { 0, 0, 0, 1, 0, 0 },
            { 0, 0, 0, 0, 1, 0 },
            { 0, 0, 0, 0, 0, 1 }
        });
    }

    private Matrix<double> CreateProcessNoiseMatrix(double dt, double processNoise)
    {
        // Process noise matrix for constant velocity model
        var dt2 = dt * dt;
        var dt3 = dt2 * dt;
        var dt4 = dt3 * dt;
        var q = processNoise;

        return Matrix<double>.Build.DenseOfArray(new double[,] {
            { q * dt4 / 4, 0, 0, q * dt3 / 2, 0, 0 },
            { 0, q * dt4 / 4, 0, 0, q * dt3 / 2, 0 },
            { 0, 0, q * dt4 / 4, 0, 0, q * dt3 / 2 },
            { q * dt3 / 2, 0, 0, q * dt2, 0, 0 },
            { 0, q * dt3 / 2, 0, 0, q * dt2, 0 },
            { 0, 0, q * dt3 / 2, 0, 0, q * dt2 }
        });
    }
}