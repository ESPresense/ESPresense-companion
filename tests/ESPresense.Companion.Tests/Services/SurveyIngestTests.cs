using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests.Services;

[TestFixture]
public class SurveyIngestTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static RobotSurveyConfig Config() => new()
    {
        Enabled = true, Device = "node:calibrate", Floor = "first", Height = 0.15,
        Transform = new RobotTransformConfig { Scale = 0.001, RotationDeg = 0, Offset = new[] { 0.0, 0.0 } },
        StationaryM = 0.2, DwellSecs = 6, MaxDwellSecs = 30, Moving = true, MaxSpeed = 1.0,
    };

    [Test]
    public void TransformScalesRotatesFlipsAndOffsets()
    {
        var cfg = Config();
        cfg.Transform = new RobotTransformConfig { Scale = 0.001, RotationDeg = 90, Offset = new[] { 1.0, 2.0 }, FlipY = true };
        var ingest = new SurveyIngest(cfg, "first", 0);
        // (1000, 500) mm -> (1, -0.5) m flipped -> rotated 90° -> (0.5, 1) -> + offset -> (1.5, 3)
        var (x, y) = ingest.Transform(1000, 500);
        Assert.That(x, Is.EqualTo(1.5).Within(1e-9));
        Assert.That(y, Is.EqualTo(3.0).Within(1e-9));
    }

    [Test]
    public void StandingStillYieldsACompleteFingerprintWithSilentNodes()
    {
        var ingest = new SurveyIngest(Config(), "first", 0.0);
        // Node c was heard a minute ago; during the dwell only a and b report.
        ingest.AddReport(T0.AddSeconds(-60), "c", -80);
        for (var s = 0; s <= 8; s++)
        {
            ingest.AddPose(T0.AddSeconds(s), 3000 + (s % 2) * 50, 4000, 90);
            ingest.AddReport(T0.AddSeconds(s), "a", -60 + (s % 2));
            ingest.AddReport(T0.AddSeconds(s), "b", -70);
            if (s < 6) Assert.That(ingest.Drain(T0.AddSeconds(s)), Is.Empty, $"nothing before the dwell completes (t={s})");
        }
        var ready = ingest.Drain(T0.AddSeconds(8));
        Assert.That(ready, Has.Count.EqualTo(1), string.Join(" | ", ready.Select(f => $"{f.Source}@{f.Taken:HH:mm:ss.fff} ({f.X:0.00},{f.Y:0.00}) nodes={string.Join(",", f.Rssi.Keys)}")));
        var f = ready[0];
        Assert.Multiple(() =>
        {
            Assert.That(f.Source, Is.EqualTo("robot"));
            Assert.That(f.Floor, Is.EqualTo("first"));
            Assert.That(f.X, Is.EqualTo(3.025).Within(0.03));
            Assert.That(f.Y, Is.EqualTo(4.0).Within(1e-6));
            Assert.That(f.Z, Is.EqualTo(0.15).Within(1e-9));
            Assert.That(f.Heading, Is.EqualTo(90));
            Assert.That(f.Rssi["a"], Is.EqualTo(-59.5).Within(0.3));
            Assert.That(f.Rssi["b"], Is.EqualTo(-70).Within(1e-9));
            Assert.That(f.Count!["b"], Is.GreaterThanOrEqualTo(6));
            Assert.That(f.StdDev!["a"], Is.GreaterThan(0));
            Assert.That(f.Silent, Is.EquivalentTo(new[] { "c" }));
        });
        for (var s = 9; s <= 14; s++)
        {
            ingest.AddPose(T0.AddSeconds(s), 3000, 4000, 90);
            ingest.AddReport(T0.AddSeconds(s), "a", -60);
            ingest.AddReport(T0.AddSeconds(s), "b", -70);
            var more = ingest.Drain(T0.AddSeconds(s));
            if (s < 14) Assert.That(more, Is.Empty, $"one sample per dwell period (t={s})");
            else Assert.That(more, Has.Count.EqualTo(1), "a long pause yields another after the next dwell period");
        }
    }

    [Test]
    public void ParkedRobotStopsProducingSamplesUntilItMoves()
    {
        var cfg = Config();
        cfg.MaxDwellSecs = 10;
        var ingest = new SurveyIngest(cfg, "first", 0);
        var produced = 0;
        for (var s = 0; s <= 40; s++)
        {
            ingest.AddPose(T0.AddSeconds(s), 1000, 1000, 0);
            ingest.AddReport(T0.AddSeconds(s), "a", -60);
            ingest.AddReport(T0.AddSeconds(s), "b", -70);
            produced += ingest.Drain(T0.AddSeconds(s)).Count;
        }
        Assert.That(ingest.Parked, Is.True);
        Assert.That(produced, Is.EqualTo(1), "one dwell sample before it counts as parked");
        // It drives off: parking clears, and a new dwell at the new spot is recorded.
        for (var s = 41; s <= 60; s++)
        {
            ingest.AddPose(T0.AddSeconds(s), s < 45 ? 1000 + (s - 40) * 500 : 3000, 1000, 0);
            ingest.AddReport(T0.AddSeconds(s), "a", -62);
            ingest.AddReport(T0.AddSeconds(s), "b", -68);
            produced += ingest.Drain(T0.AddSeconds(s)).Where(f => f.Source == "robot").Count();
            if (s == 44) Assert.That(ingest.Parked, Is.False, "driving off clears parking");
        }
        // It parks again at the new spot once it has stood there past max_dwell_secs; the dwell there was recorded first.
        Assert.That(ingest.Parked, Is.True);
        Assert.That(produced, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void DrivingStraightYieldsBinsPlacedBetweenPoses()
    {
        var ingest = new SurveyIngest(Config(), "first", 0);
        // Poses every 4 s along x at 0.5 m/s (2 m per pose), reports every second from a and b.
        for (var s = 0; s <= 40; s++)
        {
            if (s % 4 == 0) ingest.AddPose(T0.AddSeconds(s), s * 500, 2000, 0);
            ingest.AddReport(T0.AddSeconds(s), "a", -60 - s * 0.5);
            ingest.AddReport(T0.AddSeconds(s), "b", -80 + s * 0.5);
        }
        var bins = ingest.Drain(T0.AddSeconds(40)).Where(f => f.Source == "moving").ToList();
        Assert.That(bins, Has.Count.GreaterThanOrEqualTo(20));
        foreach (var b in bins)
        {
            var expectedX = (b.Taken!.Value - T0).TotalSeconds * 0.5;
            Assert.That(b.X, Is.EqualTo(expectedX).Within(0.05), $"bin at {b.Taken} placed by interpolation");
            Assert.That(b.Y, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(b.Silent, Is.Null, "a bin says nothing about nodes that did not report in it");
            Assert.That(b.Rssi.Keys, Is.EquivalentTo(new[] { "a", "b" }));
        }
        Assert.That(ingest.MovingSamples, Is.EqualTo(bins.Count));
    }

    [Test]
    public void TurnsAndSpeedingAreNotPlaced()
    {
        var ingest = new SurveyIngest(Config(), "first", 0);
        // A right-angle turn: (0,0) -> (2,0) -> (2,2); the bin between the last two poses is in the turn.
        ingest.AddPose(T0, 0, 0, 0);
        ingest.AddPose(T0.AddSeconds(4), 2000, 0, 0);
        ingest.AddPose(T0.AddSeconds(8), 2000, 2000, 90);
        Assert.That(ingest.Place(T0.AddSeconds(6)), Is.Null, "the pose before does not lie on the line through the turn");
        Assert.That(ingest.Place(T0.AddSeconds(2)), Is.Not.Null, "the first leg has no earlier pose to disagree");

        var fast = new SurveyIngest(Config(), "first", 0);
        fast.AddPose(T0, 0, 0, 0);
        fast.AddPose(T0.AddSeconds(2), 6000, 0, 0); // 3 m/s
        Assert.That(fast.Place(T0.AddSeconds(1)), Is.Null, "too fast to trust the interpolation");

        var still = new SurveyIngest(Config(), "first", 0);
        still.AddPose(T0, 0, 0, 0);
        still.AddPose(T0.AddSeconds(4), 20, 0, 0);
        Assert.That(still.Place(T0.AddSeconds(2)), Is.Null, "standing still is a dwell, not a bin");
    }

    [Test]
    public void AccuracyWindowSummarises()
    {
        var w = new AccuracyService.Window(3);
        Assert.That(w.Summarize(), Is.Null);
        foreach (var e in new[] { 5.0, 1.0, 2.0, 3.0 }) w.Add(e);
        var s = w.Summarize()!;
        Assert.That(s.Count, Is.EqualTo(3), "capacity drops the oldest");
        Assert.That(s.Median, Is.EqualTo(2.0));
        Assert.That(s.P90, Is.EqualTo(3.0));
    }
}
