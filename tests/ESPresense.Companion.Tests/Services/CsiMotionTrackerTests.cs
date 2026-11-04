using System;
using ESPresense.Events;
using ESPresense.Services;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

[TestFixture]
public class CsiMotionTrackerTests
{
    private static CsiMotionTracker CreateTracker(out Mock<IMqttCoordinator> mqtt)
    {
        mqtt = new Mock<IMqttCoordinator>();
        mqtt.SetupAllProperties();
        mqtt.SetupAdd(m => m.NodeCsiVarianceReceivedAsync += It.IsAny<Func<NodeCsiVarianceReceivedEventArgs, Task>>());
        return new CsiMotionTracker(mqtt.Object);
    }

    [Test]
    public void GetMotion_UnknownNode_ReturnsNull()
    {
        var tracker = CreateTracker(out _);
        Assert.That(tracker.GetMotion("never-seen", DateTime.UtcNow), Is.Null);
    }

    [Test]
    public void Baseline_ConvergesFromColdStart_TowardTheLowReadings()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var rnd = new Random(42);

        // Empty-room noise around 24, no motion, every 5s.
        DateTime now = start;
        for (var i = 0; i < 200; i++)
        {
            now = start.AddSeconds(i * 5);
            tracker.Observe("node-1", 24 + rnd.NextDouble() * 2 - 1, now);
        }

        var motion = tracker.GetMotion("node-1", now);
        Assert.That(motion, Is.Not.Null);
        Assert.That(motion!.Value.Baseline, Is.InRange(22.5, 25.0), "baseline should settle near the low end of the empty-room noise band");
        Assert.That(motion.Value.IsMotion, Is.False);
    }

    [Test]
    public void Baseline_TracksSlowDriftOverHours()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

        // One real node drifted ~23 -> ~30 over several hours (empty room, no motion).
        const int totalSamples = 6 * 60 * 12; // 6 hours at 5s cadence
        DateTime now = start;
        for (var i = 0; i < totalSamples; i++)
        {
            var driftFraction = (double)i / totalSamples;
            var value = 23 + driftFraction * 7;
            now = start.AddSeconds(i * 5);
            tracker.Observe("node-1", value, now);
        }

        var motion = tracker.GetMotion("node-1", now);
        Assert.That(motion, Is.Not.Null);
        // The window only holds the last 4 hours, so the 10th-percentile baseline trails the current
        // ~30 reading (it must, or a brief motion spike late in the window would drag it up too) while
        // still having moved well past the cold-start ~23 -- proof it rides the drift rather than anchoring.
        Assert.That(motion!.Value.Baseline, Is.InRange(25.0, 29.0), "baseline must track a slow multi-hour drift, lagging but not stuck at the cold-start level");
        Assert.That(motion.Value.IsMotion, Is.False, "a slow drift is not motion");
    }

    [Test]
    public void SingleWindowSpike_ThatRevertsNextWindow_IsNotMotion()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var t = start;

        for (var i = 0; i < 50; i++)
        {
            tracker.Observe("node-1", 24, t);
            t = t.AddSeconds(5);
        }

        // One window spikes 10x+...
        tracker.Observe("node-1", 260, t);
        t = t.AddSeconds(5);
        Assert.That(tracker.GetMotion("node-1", t)!.Value.IsMotion, Is.False, "a single elevated window must not count as motion yet");

        // ...then reverts immediately: the spike must never have been counted as sustained motion.
        tracker.Observe("node-1", 24, t);
        Assert.That(tracker.GetMotion("node-1", t)!.Value.IsMotion, Is.False, "a spike that reverts within one window must be rejected as motion");
    }

    [Test]
    public void SustainedElevation_AcrossTwoConsecutiveWindows_IsMotion()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var t = start;

        for (var i = 0; i < 50; i++)
        {
            tracker.Observe("node-1", 24, t);
            t = t.AddSeconds(5);
        }

        // Reference case: two separate 5s windows both read far above baseline (1143, 1230) while the
        // room's occupancy sensor was off. This must register as motion: it is sustained, not a blip.
        tracker.Observe("node-1", 1143, t);
        t = t.AddSeconds(5);
        Assert.That(tracker.GetMotion("node-1", t)!.Value.IsMotion, Is.False, "first elevated window alone is not yet sustained");

        tracker.Observe("node-1", 1230, t);
        Assert.That(tracker.GetMotion("node-1", t)!.Value.IsMotion, Is.True, "two consecutive elevated windows must register as sustained motion");
    }

    [Test]
    public void MotionScore_IsRatioOfCurrentToBaseline_NeverAFixedThreshold()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var t = start;
        for (var i = 0; i < 50; i++)
        {
            tracker.Observe("low-baseline-node", 10, t);
            tracker.Observe("high-baseline-node", 34, t);
            t = t.AddSeconds(5);
        }

        // A reading that would be "motion" for the low-baseline node (2x) must also read as motion for
        // the high-baseline node at its own 2x, even though the raw values differ by more than 3x.
        tracker.Observe("low-baseline-node", 20, t);
        tracker.Observe("high-baseline-node", 68, t);
        t = t.AddSeconds(5);
        tracker.Observe("low-baseline-node", 20, t);
        tracker.Observe("high-baseline-node", 68, t);

        Assert.That(tracker.GetMotion("low-baseline-node", t)!.Value.IsMotion, Is.True);
        Assert.That(tracker.GetMotion("high-baseline-node", t)!.Value.IsMotion, Is.True);
    }

    [Test]
    public void GetMotion_ExpiresMotion_WhenNodeStopsReportingPastTheInactivityTimeout()
    {
        var tracker = CreateTracker(out _);
        var start = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
        var t = start;

        for (var i = 0; i < 50; i++)
        {
            tracker.Observe("node-1", 24, t);
            t = t.AddSeconds(5);
        }

        // Two consecutive elevated windows: genuinely sustained motion as of the last reading.
        tracker.Observe("node-1", 260, t);
        t = t.AddSeconds(5);
        tracker.Observe("node-1", 260, t);
        Assert.That(tracker.GetMotion("node-1", t)!.Value.IsMotion, Is.True, "two consecutive elevated windows is motion right after they arrive");

        // The node then goes offline (no more Observe calls). Querying well past the inactivity timeout
        // must not keep reporting motion from a sample that is no longer current.
        var longAfter = t + TimeSpan.FromMinutes(5);
        Assert.That(tracker.GetMotion("node-1", longAfter)!.Value.IsMotion, Is.False,
            "a node that stopped reporting past the inactivity timeout must not keep its room stuck in motion");
    }
}
