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
        Assert.That(velocity.Length, Is.LessThanOrEqualTo(KalmanFilterSettings.Default.MaxVelocity + 1e-6));
    }

    [Test]
    public void Update_WalkingPace_KeepsUp()
    {
        // A person walking 1.4 m/s, one exact fix a second: the default filter has to follow, not trail behind
        var kalman = new KalmanLocation();
        var t0 = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        for (var s = 0; s <= 20; s++)
            kalman.Update(new Point3D(1.4 * s, 0, 0), t0.AddSeconds(s));

        Assert.That(kalman.Location.X, Is.EqualTo(28).Within(0.5));
    }

    [Test]
    public void Update_WalkingPaceWithNoisyFixes_DoesNotTrail()
    {
        // Fixes alternate a metre either side of the walker. A jump judged against the last estimate also counts the
        // walking, so noisy fixes look like jumps, get distrusted, and the estimate falls behind.
        var kalman = new KalmanLocation();
        var t0 = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var lag = 0.0;
        for (var s = 0; s <= 30; s++)
        {
            var truth = 1.4 * s;
            kalman.Update(new Point3D(truth + (s % 2 == 1 ? 1 : -1), 0, 0), t0.AddSeconds(s));
            if (s > 20) lag += (truth - kalman.Location.X) / 10;
        }

        Assert.That(lag, Is.LessThan(0.3));
    }

    [Test]
    public void Update_AfterLongGap_StartsOverAtTheFix()
    {
        var kalman = new KalmanLocation();
        var t0 = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        for (var s = 0; s <= 5; s++)
            kalman.Update(new Point3D(s, 0, 0), t0.AddSeconds(s)); // walking +x

        var fix = new Point3D(2, 8, 0);
        kalman.Update(fix, t0.AddMinutes(5)); // back after five minutes away

        Assert.That(kalman.Location.DistanceTo(fix), Is.LessThan(1e-6));
        Assert.That(kalman.Velocity.Length, Is.EqualTo(0).Within(1e-6));
    }

    private static void SetLastLocationUpdate(KalmanLocation kalman, DateTime value)
    {
        var field = typeof(KalmanLocation).GetField("_lastLocationUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field!.SetValue(kalman, value);
    }
}
