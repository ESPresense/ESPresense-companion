using System;
using System.Reflection;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class KalmanLocationTests
{
    [Test]
    public void GetPrediction_WhenUninitialized_ReturnsCurrentLocation()
    {
        var kalman = new KalmanLocation();

        var (predictedLocation, covariance) = kalman.GetPrediction();

        Assert.That(predictedLocation.X, Is.EqualTo(0).Within(1e-6));
        Assert.That(predictedLocation.Y, Is.EqualTo(0).Within(1e-6));
        Assert.That(predictedLocation.Z, Is.EqualTo(0).Within(1e-6));
        Assert.That(covariance.RowCount, Is.EqualTo(3));
        Assert.That(covariance.ColumnCount, Is.EqualTo(3));
        Assert.That(covariance[0, 0], Is.EqualTo(1).Within(1e-6));
        Assert.That(covariance[1, 1], Is.EqualTo(1).Within(1e-6));
        Assert.That(covariance[2, 2], Is.EqualTo(1).Within(1e-6));
    }

    [Test]
    public void Update_FirstMeasurement_SetsLocation()
    {
        var kalman = new KalmanLocation();
        var measurement = new Point3D(1.2, -3.4, 0.6);

        var result = kalman.Update(measurement);

        Assert.That(kalman.Location.X, Is.EqualTo(measurement.X).Within(1e-6));
        Assert.That(kalman.Location.Y, Is.EqualTo(measurement.Y).Within(1e-6));
        Assert.That(kalman.Location.Z, Is.EqualTo(measurement.Z).Within(1e-6));
        Assert.That(result.X, Is.EqualTo(measurement.X).Within(1e-6));
        Assert.That(result.Y, Is.EqualTo(measurement.Y).Within(1e-6));
        Assert.That(result.Z, Is.EqualTo(measurement.Z).Within(1e-6));
    }

    [Test]
    public void Update_LargeJump_ProducesSmoothedLocation()
    {
        var kalman = new KalmanLocation();
        var origin = new Point3D(0, 0, 0);
        kalman.Update(origin);

        SetLastLocationUpdate(kalman, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(1)));
        var predicted = kalman.GetPrediction().PredictedLocation;
        SetLastLocationUpdate(kalman, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(1)));

        var measurement = new Point3D(10, 0, 0);
        var updated = kalman.Update(measurement);

        Assert.That(updated.X, Is.GreaterThan(predicted.X));
        Assert.That(updated.X, Is.LessThan(measurement.X));
        Assert.That(updated.DistanceTo(predicted), Is.LessThan(predicted.DistanceTo(measurement)));
    }

    [Test]
    public void Update_LargeJump_ClampsVelocity()
    {
        var kalman = new KalmanLocation();
        kalman.Update(new Point3D(0, 0, 0));

        SetLastLocationUpdate(kalman, DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(1)));
        kalman.Update(new Point3D(10, 5, 0));

        var velocity = kalman.Velocity;
        Assert.That(velocity.Length, Is.LessThanOrEqualTo(0.5 + 1e-6));
    }

    private static void SetLastLocationUpdate(KalmanLocation kalman, DateTime value)
    {
        var field = typeof(KalmanLocation).GetField("_lastLocationUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field!.SetValue(kalman, value);
    }
}
