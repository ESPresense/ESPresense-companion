using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The spot score (#1868): a calibration judged on devices at known positions, each spot's level fitted, so only how
/// the receivers are calibrated relative to each other counts. All ids are made up.
/// </summary>
public class SpotScoreTests
{
    private static readonly (string Id, Point3D At)[] Receivers =
    {
        ("rx-a", new Point3D(0, 0, 1)),
        ("rx-b", new Point3D(6, 0, 1)),
        ("rx-c", new Point3D(0, 5, 1)),
        ("rx-d", new Point3D(6, 5, 1)),
    };

    private const double Absorption = 2.8;

    /// <summary>
    /// A device at <paramref name="at"/> with level <paramref name="level"/> as each receiver hears it in raw RSSI, exactly on
    /// the model; <paramref name="rxGain"/> adds a receiver's own hardware offset (what its rxAdj should cancel).
    /// </summary>
    private static KnownPositionSpot Spot(string id, Point3D at, double level, Dictionary<string, double>? rxGain = null) =>
        new(id, at, Receivers.Select(r => new SpotReading(r.Id, r.At,
            level + (rxGain?.GetValueOrDefault(r.Id) ?? 0) - 10 * Absorption * Math.Log10(r.At.DistanceTo(at)))).ToList());

    private static (double, double) Calibrated(string _) => (0, Absorption);

    [Test]
    public void ExactModel_ScoresZero_WhateverTheDevicesLevel()
    {
        // The level is fitted per spot, so a device's rssi@1m (often a guess) cannot move the score.
        foreach (var level in new[] { -59.0, -70.0, -45.0 })
        {
            var scored = SpotScore.Evaluate(new[] { Spot("s1", new Point3D(2, 2, 0.3), level) }, Calibrated);
            Assert.That(scored["s1"].Sse, Is.EqualTo(0).Within(1e-9), $"level {level}");
            Assert.That(scored["s1"].Readings, Is.EqualTo(4));
        }
    }

    [Test]
    public void OneReceiverOff_IsSeen_AndTheProposalThatFixesItWinsEverySpot()
    {
        // rx-b hears 6 dB hot. The incumbent leaves its rxAdj at 0; the proposal sets it to 6, which cancels it.
        var gain = new Dictionary<string, double> { ["rx-b"] = 6 };
        var spots = new[] { Spot("s1", new Point3D(2, 2, 0.3), -60, gain), Spot("s2", new Point3D(4, 3, 0.3), -66, gain), Spot("s3", new Point3D(1, 4, 0.3), -55, gain) };

        var incumbent = SpotScore.Evaluate(spots, Calibrated);
        var proposal = SpotScore.Evaluate(spots, id => (id == "rx-b" ? 6 : 0, Absorption));

        Assert.That(incumbent.Values.All(v => v.Sse > 1), Is.True, "a 6 dB error on one receiver shows on every spot it hears");
        Assert.That(proposal.Values.All(v => v.Sse < 1e-9), Is.True);

        var baseline = SpotScore.Report(incumbent);
        var report = SpotScore.Report(proposal, incumbent);
        Assert.That(baseline.Better, Is.Null, "the incumbent is not compared with itself");
        Assert.That(baseline.Rms, Is.GreaterThan(2));
        Assert.That((report.Spots, report.Readings, report.Better, report.Worse), Is.EqualTo((3, 12, (int?)3, (int?)0)));
        Assert.That(report.Rms, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void UniformShiftOfEveryReceiver_DoesNotChangeTheScore()
    {
        // Moving every rxAdj by the same amount is the gauge: it changes every device's range, but not how the
        // receivers agree. That is the anchors' job (the rssi@1m they are held at), not this score's.
        var gain = new Dictionary<string, double> { ["rx-c"] = -4 };
        var spots = new[] { Spot("s1", new Point3D(2, 2, 0.3), -60, gain), Spot("s2", new Point3D(5, 1, 0.3), -62, gain) };

        var a = SpotScore.Evaluate(spots, Calibrated);
        var b = SpotScore.Evaluate(spots, _ => (5, Absorption));

        foreach (var id in a.Keys) Assert.That(b[id].Sse, Is.EqualTo(a[id].Sse).Within(1e-9));
    }

    [Test]
    public void FromAnchors_AveragesRawRssiPerReceiver_AndSkipsNodesAndThinSpots()
    {
        var anchor = new OptNode { Id = "anchor-1", Location = new Point3D(3, 2, 0.5), IsAnchor = true };
        var lonely = new OptNode { Id = "anchor-2", Location = new Point3D(1, 1, 0.5), IsAnchor = true };
        var node = new OptNode { Id = "rx-d", Location = Receivers[3].At };
        OptNode Rx(int i) => new() { Id = Receivers[i].Id, Location = Receivers[i].At };

        var s1 = new OptimizationSnapshot();
        var s2 = new OptimizationSnapshot();
        // Published rssi includes the receiver's rxAdj at the time: raw = rssi + rxAdj, so -70 with rxAdj 4 is raw -66.
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(0), Rssi = -70, RssiRxAdj = 4 });
        s2.Measures.Add(new Measure { Tx = anchor, Rx = Rx(0), Rssi = -68, RssiRxAdj = 0 }); // raw -68: a later rxAdj change
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(1), Rssi = -75 });                // no rxAdj recorded: taken as 0
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(2), Rssi = -72, RssiRxAdj = -2 });
        s1.Measures.Add(new Measure { Tx = lonely, Rx = Rx(0), Rssi = -60 });                // heard by one receiver only
        s1.Measures.Add(new Measure { Tx = node, Rx = Rx(0), Rssi = -65 });                  // node-to-node: not a spot

        var spots = SpotScore.FromAnchors(new[] { s1, s2 });

        var spot = spots.Single();
        Assert.That(spot.Id, Is.EqualTo("anchor-1"));
        Assert.That(spot.Location, Is.EqualTo(anchor.Location));
        var raw = spot.Readings.ToDictionary(r => r.RxId, r => r.RawRssi);
        Assert.That(raw, Is.EqualTo(new Dictionary<string, double> { ["rx-a"] = -67, ["rx-b"] = -75, ["rx-c"] = -74 }));
    }

    [Test]
    public void Cycle_ReportsTheSpotScore_OfTheAnchorsInTheEvalSet()
    {
        // Node-to-node links that fit the incumbent exactly, plus one anchor heard 6 dB hot by rx-b. A proposal that only
        // fixes rx-b's rxAdj is scored on the spot. Report-only: the gate never reads it.
        var nodes = Receivers.Select(r => new OptNode { Id = r.Id, Location = r.At }).ToList();
        var anchor = new OptNode { Id = "anchor-1", Location = new Point3D(2, 2, 0.5), IsAnchor = true };
        OptimizationSnapshot Snap(DateTime t)
        {
            var s = new OptimizationSnapshot { Timestamp = t };
            foreach (var tx in nodes)
                foreach (var rx in nodes.Where(n => n != tx))
                    s.Measures.Add(new Measure { Tx = tx, Rx = rx, RefRssi = -59, RssiRxAdj = 0, Rssi = -59 - 10 * 2.7 * Math.Log10(tx.Location.DistanceTo(rx.Location)) });
            foreach (var rx in nodes)
                s.Measures.Add(new Measure { Tx = anchor, Rx = rx, RefRssi = -62, RssiRxAdj = 0,
                    Rssi = -62 + (rx.Id == "rx-b" ? 6 : 0) - 10 * 2.7 * Math.Log10(anchor.Location.DistanceTo(rx.Location)) });
            return s;
        }
        var t0 = DateTime.UtcNow;
        var held = new List<OptimizationSnapshot> { Snap(t0.AddMinutes(-2)), Snap(t0.AddMinutes(-1)), Snap(t0) };

        var result = OptimizationCycle.Evaluate(Snap(t0.AddMinutes(-10)), held, new ConfigOptimization { Enabled = true },
            new List<IOptimizer> { new FixRxB() }, id => new NodeSettings(id), dryRun: true, t0);

        var report = result.Report;
        Assert.That(report.BaselineSpots, Is.Not.Null);
        Assert.That(report.BaselineSpots!.Spots, Is.EqualTo(1));
        Assert.That(report.BaselineSpots.Rms, Is.GreaterThan(2), "rx-b's 6 dB shows on the anchor");
        var spots = report.Proposals.Single().Spots!;
        Assert.That(spots.Better, Is.EqualTo(1));
        Assert.That(spots.Rms, Is.EqualTo(0).Within(1e-6));

        var withoutSpots = OptimizationCycle.Evaluate(Snap(t0.AddMinutes(-10)), held.Select(s => new OptimizationSnapshot
            { Timestamp = s.Timestamp, Measures = s.Measures.Where(m => !m.Tx.IsAnchor).ToList() }).ToList(),
            new ConfigOptimization { Enabled = true }, new List<IOptimizer> { new FixRxB() }, id => new NodeSettings(id), dryRun: true, t0);
        Assert.That(withoutSpots.Report.BaselineSpots, Is.Null, "no anchors, no spot score");
        Assert.That(withoutSpots.Report.Proposals.Single().Spots, Is.Null);
    }

    /// <summary>Raises rx-b's rxAdj to 6 dB (absorption 2.7, as the links were generated with); touches nothing else.</summary>
    private sealed class FixRxB : IOptimizer
    {
        public string Name => "FixRxB";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
            => new() { Nodes = { ["rx-b"] = new ProposedValues { RxAdjRssi = 6, Absorption = 2.7 } } };
    }
}
