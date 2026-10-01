using ESPresense.Services.Occupancy;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services.Occupancy;

public class CsiOccupancyDetectorTests
{
    // Mirrors the amp_spread contract from ESPA-173's synthetic ECF1 corpus generator
    // (tests/ESPresense.Companion.Simulation/AccuracyHarness/Csi/ecf1.py: gen_fixture):
    // vacant scenes use spread=6, occupied scenes use spread=18. With this generator, the
    // corrected temporal per-subcarrier variance formula reproduces occupancy_fusion.py's
    // documented synthetic-corpus clusters (vacant ~0.043-0.047, occupied ~0.117-0.131).
    private static sbyte[] GenerateIq(int seed, int spread, int subcarriers = 64)
    {
        var rng = new Random(seed);
        var iq = new sbyte[subcarriers * 2];
        for (var i = 0; i < iq.Length; i++)
            iq[i] = (sbyte)rng.Next(-spread, spread + 1);
        return iq;
    }

    private static CsiOccupancySignal ObserveWindow(CsiOccupancyDetector detector, string nodeId, int spread, int frames = 12)
    {
        var now = DateTimeOffset.UtcNow;
        var signal = default(CsiOccupancySignal);
        for (var i = 0; i < frames; i++)
            signal = detector.Observe(nodeId, GenerateIq(seed: i, spread: spread), now.AddMilliseconds(i * 33));
        return signal;
    }

    [Test]
    public void Observe_VacantLikeAmplitudeVariance_ScoresBelowThreshold()
    {
        var signal = ObserveWindow(new CsiOccupancyDetector(), "node-1", spread: 6);

        Assert.That(signal.MotionScore, Is.LessThan(CsiOccupancyDetector.OccupiedThreshold));
        Assert.That(signal.MotionScore, Is.InRange(0.03, 0.06));
    }

    [Test]
    public void Observe_OccupiedLikeAmplitudeVariance_ScoresAboveThreshold()
    {
        var signal = ObserveWindow(new CsiOccupancyDetector(), "node-1", spread: 18);

        Assert.That(signal.MotionScore, Is.GreaterThan(CsiOccupancyDetector.OccupiedThreshold));
        Assert.That(signal.MotionScore, Is.InRange(0.10, 0.15));
    }

    [Test]
    public void Observe_TracksSeparateNodesIndependently()
    {
        var detector = new CsiOccupancyDetector();

        var quiet = ObserveWindow(detector, "quiet-node", spread: 6);
        var busy = ObserveWindow(detector, "busy-node", spread: 18);

        Assert.That(quiet.NodeId, Is.EqualTo("quiet-node"));
        Assert.That(busy.NodeId, Is.EqualTo("busy-node"));
        Assert.That(busy.MotionScore, Is.GreaterThan(quiet.MotionScore));
    }

    [Test]
    public void Observe_SingleFrame_HasNoTemporalVarianceYet()
    {
        // The feature is variance *across time* per subcarrier (esp-radar style); a lone frame
        // has nothing to compare against, so it must score 0, not the spatial spread within it.
        var detector = new CsiOccupancyDetector();

        var signal = detector.Observe("node-1", GenerateIq(1, 18), DateTimeOffset.UtcNow);

        Assert.That(signal.MotionScore, Is.EqualTo(0.0));
    }

    [Test]
    public void Observe_FramesOutsideWindow_AreEvicted()
    {
        var detector = new CsiOccupancyDetector(window: TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.UtcNow;

        detector.Observe("node-1", GenerateIq(1, 18), now);
        var later = detector.Observe("node-1", GenerateIq(2, 18), now.AddSeconds(5));

        Assert.That(later.WindowFrameCount, Is.EqualTo(1));
    }

    [Test]
    public void Observe_OddLengthPayload_Throws()
    {
        var detector = new CsiOccupancyDetector();
        Assert.Throws<ArgumentException>(() => detector.Observe("node-1", new sbyte[] { 1, 2, 3 }, DateTimeOffset.UtcNow));
    }

    [Test]
    public void Observe_EmptyPayload_Throws()
    {
        // A zero-length payload is even-length, so the odd-length guard alone lets it through;
        // left unrejected it enters the buffer with 0 subcarriers and divides variance by zero
        // (NaN), or crashes with mismatched array lengths against any other buffered frame.
        var detector = new CsiOccupancyDetector();
        Assert.Throws<ArgumentException>(() => detector.Observe("node-1", Array.Empty<sbyte>(), DateTimeOffset.UtcNow));
    }

    [Test]
    public void Observe_NonPositiveWindow_Throws()
    {
        // An unvalidated window <= 0 would evict every frame but the one just added on every
        // call, so the detector could never see more than a single frame and would always score 0.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CsiOccupancyDetector(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CsiOccupancyDetector(TimeSpan.FromMilliseconds(-1)));
    }

    [Test]
    public void Observe_OutOfOrderTimestamp_StillEvictsExpiredFrame()
    {
        // Frames arrive at t=0 and t=11s (both kept, 11s apart > window but each is within window
        // of itself/the other at insertion time -- not the point here), then a LATE t=1s frame
        // arrives after t=11s. A head-only eviction queue never re-examines the t=0 frame once
        // it's not at the head, so it can survive indefinitely. Evicting by age against the
        // latest timestamp on every call must catch it regardless of arrival order.
        var detector = new CsiOccupancyDetector(window: TimeSpan.FromSeconds(2));
        var t0 = DateTimeOffset.UtcNow;

        detector.Observe("node-1", GenerateIq(1, 18), t0);
        var afterGap = detector.Observe("node-1", GenerateIq(2, 18), t0.AddSeconds(11));
        Assert.That(afterGap.WindowFrameCount, Is.EqualTo(1), "the t=0 frame should already be expired by t=11s");

        // A frame timestamped between t0 and t0+11s, delivered out of order (after t0+11s).
        var lateArrival = detector.Observe("node-1", GenerateIq(3, 18), t0.AddSeconds(1));
        Assert.That(lateArrival.WindowFrameCount, Is.EqualTo(1),
            "the stale out-of-order frame must not pin an expired entry in the window");
    }
}
