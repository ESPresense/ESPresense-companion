using ESPresense.Services.Occupancy;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services.Occupancy;

public class CsiOccupancyDetectorTests
{
    // Mirrors the amp_spread contract from ESPA-173's synthetic ECF1 corpus generator
    // (tests/ESPresense.Companion.Simulation/AccuracyHarness/Csi/ecf1.py: gen_fixture):
    // vacant scenes use spread=6, occupied scenes use spread=18.
    private static sbyte[] GenerateIq(int seed, int spread, int subcarriers = 64)
    {
        var rng = new Random(seed);
        var iq = new sbyte[subcarriers * 2];
        for (var i = 0; i < iq.Length; i++)
            iq[i] = (sbyte)rng.Next(-spread, spread + 1);
        return iq;
    }

    [Test]
    public void Observe_VacantLikeAmplitudeVariance_YieldsLowConfidence()
    {
        var detector = new CsiOccupancyDetector();
        var now = DateTimeOffset.UtcNow;
        var signal = default(CsiOccupancySignal);

        for (var i = 0; i < 12; i++)
            signal = detector.Observe("node-1", GenerateIq(seed: i, spread: 6), now.AddMilliseconds(i * 33));

        Assert.That(signal.Confidence, Is.LessThan(0.3));
    }

    [Test]
    public void Observe_OccupiedLikeAmplitudeVariance_YieldsHighConfidence()
    {
        var detector = new CsiOccupancyDetector();
        var now = DateTimeOffset.UtcNow;
        var signal = default(CsiOccupancySignal);

        for (var i = 0; i < 12; i++)
            signal = detector.Observe("node-1", GenerateIq(seed: i, spread: 18), now.AddMilliseconds(i * 33));

        Assert.That(signal.Confidence, Is.GreaterThan(0.7));
    }

    [Test]
    public void Observe_TracksSeparateNodesIndependently()
    {
        var detector = new CsiOccupancyDetector();
        var now = DateTimeOffset.UtcNow;

        var quiet = detector.Observe("quiet-node", GenerateIq(1, 6), now);
        var busy = detector.Observe("busy-node", GenerateIq(2, 18), now);

        Assert.That(quiet.NodeId, Is.EqualTo("quiet-node"));
        Assert.That(busy.NodeId, Is.EqualTo("busy-node"));
        Assert.That(busy.Confidence, Is.GreaterThan(quiet.Confidence));
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
}
