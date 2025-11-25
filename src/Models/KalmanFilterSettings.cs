namespace ESPresense.Models;

/// <summary>
/// Immutable settings for Kalman filtering shared across all devices
/// </summary>
public class KalmanFilterSettings
{
    public double ProcessNoise { get; }
    public double MeasurementNoise { get; }
    public double MaxVelocity { get; }

    public KalmanFilterSettings(double processNoise, double measurementNoise, double maxVelocity)
    {
        ProcessNoise = processNoise;
        MeasurementNoise = measurementNoise;
        MaxVelocity = maxVelocity;
    }

    /// <summary>
    /// Default settings matching KalmanLocation defaults
    /// </summary>
    public static KalmanFilterSettings Default { get; } = new(0.01, 0.1, 0.5);

    /// <summary>
    /// Creates settings from config
    /// </summary>
    public static KalmanFilterSettings FromConfig(ConfigFiltering? config)
    {
        if (config == null) return Default;
        return new KalmanFilterSettings(config.ProcessNoise, config.MeasurementNoise, config.MaxVelocity);
    }

    public override bool Equals(object? obj)
    {
        return obj is KalmanFilterSettings other &&
               ProcessNoise == other.ProcessNoise &&
               MeasurementNoise == other.MeasurementNoise &&
               MaxVelocity == other.MaxVelocity;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ProcessNoise, MeasurementNoise, MaxVelocity);
    }
}
