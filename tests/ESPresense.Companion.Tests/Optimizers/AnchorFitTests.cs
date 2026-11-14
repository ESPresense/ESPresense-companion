using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// The anchor fit (#1868): a calibration judged on the anchors, each anchor's level fitted, so only how the receivers
/// are calibrated relative to each other counts. All ids are made up.
/// </summary>
public class AnchorFitTests
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
    /// An anchor at <paramref name="at"/> with level <paramref name="level"/> as each receiver hears it in raw RSSI, exactly on
    /// the model; <paramref name="rxGain"/> adds a receiver's own hardware offset (what its rxAdj should cancel).
    /// </summary>
    private static AnchorReadings Anchor(string id, Point3D at, double level, Dictionary<string, double>? rxGain = null) =>
        new(id, at, Receivers.Select(r => new AnchorReading(r.Id, r.At,
            level + (rxGain?.GetValueOrDefault(r.Id) ?? 0) - 10 * Absorption * Math.Log10(r.At.DistanceTo(at)))).ToList());

    private static (double, double) Calibrated(string _) => (0, Absorption);

    [Test]
    public void ExactModel_ScoresZero_WhateverTheAnchorsLevel()
    {
        // The level is fitted per anchor, so an anchor's rssi@1m (often a guess) cannot move the score.
        foreach (var level in new[] { -59.0, -70.0, -45.0 })
        {
            var scored = AnchorFit.Evaluate(new[] { Anchor("a1", new Point3D(2, 2, 0.3), level) }, Calibrated);
            Assert.That(scored["a1"].Sse, Is.EqualTo(0).Within(1e-9), $"level {level}");
            Assert.That(scored["a1"].Readings, Is.EqualTo(4));
        }
    }

    [Test]
    public void OneReceiverOff_IsSeen_AndTheProposalThatFixesItWinsEveryAnchor()
    {
        // rx-b hears 6 dB hot. The incumbent leaves its rxAdj at 0; the proposal sets it to 6, which cancels it.
        var gain = new Dictionary<string, double> { ["rx-b"] = 6 };
        var anchors = new[] { Anchor("a1", new Point3D(2, 2, 0.3), -60, gain), Anchor("a2", new Point3D(4, 3, 0.3), -66, gain), Anchor("a3", new Point3D(1, 4, 0.3), -55, gain) };

        var incumbent = AnchorFit.Evaluate(anchors, Calibrated);
        var proposal = AnchorFit.Evaluate(anchors, id => (id == "rx-b" ? 6 : 0, Absorption));

        Assert.That(incumbent.Values.All(v => v.Sse > 1), Is.True, "a 6 dB error on one receiver shows on every anchor it hears");
        Assert.That(proposal.Values.All(v => v.Sse < 1e-9), Is.True);

        var baseline = AnchorFit.Report(incumbent);
        var report = AnchorFit.Report(proposal, incumbent);
        Assert.That(baseline.Better, Is.Null, "the incumbent is not compared with itself");
        Assert.That(baseline.Rms, Is.GreaterThan(2));
        Assert.That((report.Anchors, report.Readings, report.Better, report.Worse), Is.EqualTo((3, 12, (int?)3, (int?)0)));
        Assert.That(report.Rms, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void UniformShiftOfEveryReceiver_DoesNotChangeTheScore()
    {
        // Moving every rxAdj by the same amount is the gauge: it changes every device's range, but not how the
        // receivers agree. That is the anchors' rssi@1m's job (the level they are held at), not this fit's.
        var gain = new Dictionary<string, double> { ["rx-c"] = -4 };
        var anchors = new[] { Anchor("a1", new Point3D(2, 2, 0.3), -60, gain), Anchor("a2", new Point3D(5, 1, 0.3), -62, gain) };

        var a = AnchorFit.Evaluate(anchors, Calibrated);
        var b = AnchorFit.Evaluate(anchors, _ => (5, Absorption));

        foreach (var id in a.Keys) Assert.That(b[id].Sse, Is.EqualTo(a[id].Sse).Within(1e-9));
    }

    [Test]
    public void FromSnapshots_AveragesRawRssiPerReceiver_AndSkipsNodesThinAnchorsAndUnknownRxAdj()
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
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(1), Rssi = -75, RssiRxAdj = 0 });
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(3), Rssi = -71 });                // rxAdj unknown: raw unknown, skipped
        s1.Measures.Add(new Measure { Tx = anchor, Rx = Rx(2), Rssi = -72, RssiRxAdj = -2 });
        s1.Measures.Add(new Measure { Tx = lonely, Rx = Rx(0), Rssi = -60 });                // heard by one receiver only
        s1.Measures.Add(new Measure { Tx = node, Rx = Rx(0), Rssi = -65 });                  // node-to-node: not an anchor

        var anchors = AnchorFit.FromSnapshots(new[] { s1, s2 });

        var only = anchors.Single();
        Assert.That(only.Id, Does.StartWith("anchor-1@"));
        Assert.That(only.Location, Is.EqualTo(anchor.Location));
        var raw = only.Readings.ToDictionary(r => r.RxId, r => r.RawRssi);
        Assert.That(raw, Is.EqualTo(new Dictionary<string, double> { ["rx-a"] = -67, ["rx-b"] = -75, ["rx-c"] = -74 }));
    }

    [Test]
    public void FromSnapshots_AnAnchorMovedDuringTheWindow_IsScoredAtEachPosition()
    {
        // Each snapshot carries its own OptNode, so an anchor moved between snapshots shows up at a different location.
        var before = new OptNode { Id = "anchor-1", Location = new Point3D(1, 1, 0.5), IsAnchor = true };
        var after = new OptNode { Id = "anchor-1", Location = new Point3D(5, 4, 0.5), IsAnchor = true };
        OptNode Rx(int i) => new() { Id = Receivers[i].Id, Location = Receivers[i].At };
        var s1 = new OptimizationSnapshot(); var s2 = new OptimizationSnapshot();
        for (var i = 0; i < 3; i++)
        {
            s1.Measures.Add(new Measure { Tx = before, Rx = Rx(i), Rssi = -60 - i, RssiRxAdj = 0 });
            s2.Measures.Add(new Measure { Tx = after, Rx = Rx(i), Rssi = -70 - i, RssiRxAdj = 0 });
        }

        var anchors = AnchorFit.FromSnapshots(new[] { s1, s2 });

        Assert.That(anchors.Select(s => s.Location), Is.EquivalentTo(new[] { before.Location, after.Location }));
        Assert.That(anchors.Single(s => s.Location == before.Location).Readings.Select(r => r.RawRssi), Is.EquivalentTo(new[] { -60.0, -61, -62 }));
    }

    [Test]
    public void FromSnapshots_PositionsThatOnlyDifferAfterRounding_StayApart()
    {
        // 3 mm apart: both round to the same centimetre, but they are two positions and both must be scored.
        var here = new OptNode { Id = "anchor-1", Location = new Point3D(1.001, 1, 0.5), IsAnchor = true };
        var there = new OptNode { Id = "anchor-1", Location = new Point3D(1.004, 1, 0.5), IsAnchor = true };
        OptNode Rx(int i) => new() { Id = Receivers[i].Id, Location = Receivers[i].At };
        var s1 = new OptimizationSnapshot(); var s2 = new OptimizationSnapshot();
        for (var i = 0; i < 3; i++)
        {
            s1.Measures.Add(new Measure { Tx = here, Rx = Rx(i), Rssi = -60 - i, RssiRxAdj = 0 });
            s2.Measures.Add(new Measure { Tx = there, Rx = Rx(i), Rssi = -70 - 3 * i, RssiRxAdj = 0 });
        }

        var anchors = AnchorFit.FromSnapshots(new[] { s1, s2 });

        Assert.That(anchors.Select(a => a.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(AnchorFit.Evaluate(anchors, Calibrated), Has.Count.EqualTo(2), "neither position overwrites the other");
    }

    [Test]
    public void Cycle_ReportsTheAnchorFit_OfTheAnchorsInTheEvalSet()
    {
        // Node-to-node links that fit the incumbent exactly, plus one anchor heard 6 dB hot by rx-b. A proposal that only
        // fixes rx-b's rxAdj is scored on the anchor. Report-only: the gate never reads it.
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
        Assert.That(report.BaselineAnchors, Is.Not.Null);
        Assert.That(report.BaselineAnchors!.Anchors, Is.EqualTo(1));
        Assert.That(report.BaselineAnchors.Rms, Is.GreaterThan(2), "rx-b's 6 dB shows on the anchor");
        var fit = report.Proposals.Single().Anchors!;
        Assert.That(fit.Better, Is.EqualTo(1));
        Assert.That(fit.Rms, Is.EqualTo(0).Within(1e-6));

        var withoutAnchors = OptimizationCycle.Evaluate(Snap(t0.AddMinutes(-10)), held.Select(s => new OptimizationSnapshot
            { Timestamp = s.Timestamp, Measures = s.Measures.Where(m => !m.Tx.IsAnchor).ToList() }).ToList(),
            new ConfigOptimization { Enabled = true }, new List<IOptimizer> { new FixRxB() }, id => new NodeSettings(id), dryRun: true, t0);
        Assert.That(withoutAnchors.Report.BaselineAnchors, Is.Null, "no anchors, no anchor fit");
        Assert.That(withoutAnchors.Report.Proposals.Single().Anchors, Is.Null);
    }

    /// <summary>Raises rx-b's rxAdj to 6 dB (absorption 2.7, as the links were generated with); touches nothing else.</summary>
    private sealed class FixRxB : IOptimizer
    {
        public string Name => "FixRxB";

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
            => new() { Nodes = { ["rx-b"] = new ProposedValues { RxAdjRssi = 6, Absorption = 2.7 } } };
    }
}
