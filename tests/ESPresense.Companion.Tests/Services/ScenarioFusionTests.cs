using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Services;

/// <summary>Fusion runs on a bare <see cref="Device"/>: no MQTT, history store or event dispatcher.</summary>
public class ScenarioFusionTests
{
    private sealed class At(Point3D location, int? confidence, bool moved = true, bool current = true) : ILocate
    {
        public bool Locate(Scenario scenario)
        {
            scenario.ResetLocation(location);
            if (!current) scenario.LastHit = DateTime.UtcNow.AddMinutes(-5);
            scenario.Confidence = confidence;
            return moved;
        }
    }

    private static Device NewDevice(Point3D? kalmanAt = null)
    {
        var device = new Device("test:fusion", null, TimeSpan.FromSeconds(30));
        device.KalmanFilter.Reset(kalmanAt ?? new Point3D());
        return device;
    }

    private static Scenario Add(Device device, string name, ILocate locator)
    {
        var scenario = new Scenario(null, locator, name);
        device.Scenarios.Add(scenario);
        return scenario;
    }

    [Test]
    public void SingleScenario_IsBest_AndCountsAsMoved()
    {
        var device = NewDevice(kalmanAt: new Point3D(1, 2, 0));
        var only = Add(device, "only", new At(new Point3D(1, 2, 0), 60));
        var now = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var result = ScenarioFusion.Update(device, new ConfigFiltering(), now);

        Assert.That(result.Best, Is.SameAs(only));
        Assert.That(result.Moved, Is.EqualTo(1));
        Assert.That(device.BestScenario, Is.SameAs(only));
        Assert.That(device.LastCalculated, Is.EqualTo(now));
        Assert.That(only.Probability, Is.EqualTo(1.0).Within(1e-12));
        Assert.That(only.WeightedConfidence, Is.EqualTo(60).Within(1e-9));
    }

    [Test]
    public void NoCurrentScenario_HasNoBest_AndZeroWeight()
    {
        var device = NewDevice();
        var stale = Add(device, "stale", new At(new Point3D(1, 2, 0), 60, moved: false, current: false));

        var result = ScenarioFusion.Update(device, new ConfigFiltering(), DateTime.UtcNow);

        Assert.That(result.Best, Is.Null);
        Assert.That(result.Moved, Is.Zero);
        Assert.That(device.BestScenario, Is.Null);
        Assert.That(stale.WeightedConfidence, Is.Zero);
    }

    [Test]
    public void FarScenario_IsPenalisedByMotionConsistency()
    {
        // Equal raw confidence; the first feeds the Kalman filter, so the prediction sits on it
        var filtering = new ConfigFiltering { MotionSigma = 2.0, SmoothingWeight = 0.7 };
        var device = NewDevice();
        var near = Add(device, "near", new At(new Point3D(0, 0, 0), 50));
        var far = Add(device, "far", new At(new Point3D(5, 0, 0), 50));

        var result = ScenarioFusion.Update(device, filtering, DateTime.UtcNow);

        var penalty = Math.Exp(-25.0 / (2 * 2.0 * 2.0));
        Assert.That(near.WeightedConfidence, Is.EqualTo(50).Within(1e-6));
        Assert.That(far.WeightedConfidence, Is.EqualTo(50 * penalty).Within(1e-6));
        Assert.That(result.Best, Is.SameAs(near));
        Assert.That(near.Probability + far.Probability, Is.EqualTo(1.0).Within(1e-12));
        Assert.That(near.Probability, Is.GreaterThan(far.Probability));
    }

    [Test]
    public void Probabilities_AreSmoothedOverRepeatedPasses()
    {
        var filtering = new ConfigFiltering { MotionSigma = 2.0, SmoothingWeight = 0.7 };
        var device = NewDevice();
        var near = Add(device, "near", new At(new Point3D(0, 0, 0), 50));
        Add(device, "far", new At(new Point3D(5, 0, 0), 50));

        var penalty = Math.Exp(-25.0 / 8.0);
        var q = 1 / (1 + penalty); // the share near earns each pass

        // Both start at probability 1; the first pass renormalises, later ones already sum to 1
        var expected = (0.7 + 0.3 * q) / 1.7;
        var seen = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            ScenarioFusion.Update(device, filtering, DateTime.UtcNow);
            seen.Add(near.Probability);
            Assert.That(near.Probability, Is.EqualTo(expected).Within(1e-6), $"pass {i + 1}");
            expected = 0.7 * expected + 0.3 * q;
        }

        Assert.That(seen, Is.Ordered.Ascending, "near converges on its share instead of jumping to it");
        Assert.That(seen[^1], Is.LessThan(q));
    }

    [Test]
    public void ProbabilityTie_GoesToHigherConfidence()
    {
        // SmoothingWeight 1 keeps the priors, so both scenarios sit at the same probability
        var filtering = new ConfigFiltering { SmoothingWeight = 1.0 };
        var device = NewDevice();
        var low = Add(device, "low", new At(new Point3D(1, 1, 0), 40));
        var high = Add(device, "high", new At(new Point3D(1, 1, 0), 60));

        var result = ScenarioFusion.Update(device, filtering, DateTime.UtcNow);

        Assert.That(low.Probability, Is.EqualTo(high.Probability));
        Assert.That(result.Best, Is.SameAs(high));
    }

    [Test]
    public void ProbabilityAndConfidenceTie_GoesToEarlierScenario()
    {
        var filtering = new ConfigFiltering { SmoothingWeight = 1.0 };
        var device = NewDevice();
        var first = Add(device, "first", new At(new Point3D(1, 1, 0), 50));
        Add(device, "second", new At(new Point3D(1, 1, 0), 50));

        Assert.That(ScenarioFusion.Update(device, filtering, DateTime.UtcNow).Best, Is.SameAs(first));
    }

    [Test]
    public void Moved_CountsOnlyScenariosWhoseLocatorMoved()
    {
        var device = NewDevice();
        Add(device, "a", new At(new Point3D(1, 1, 0), 50, moved: true));
        Add(device, "b", new At(new Point3D(1, 1, 0), 50, moved: false));
        Add(device, "c", new At(new Point3D(1, 1, 0), 50, moved: true));

        Assert.That(ScenarioFusion.Update(device, new ConfigFiltering(), DateTime.UtcNow).Moved, Is.EqualTo(2));
    }
}
