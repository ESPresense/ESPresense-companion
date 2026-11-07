using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// optimization.judge: spots (#1868): proposals are accepted on devices at known positions, not on node-to-node links.
/// The house: four receivers, of which rx-b hears everything 6 dB hot (a hardware offset, so nodes and devices alike),
/// and anchored devices spread around it. All ids are made up.
/// </summary>
public class SpotJudgeTests
{
    private static readonly (string Id, Point3D At)[] Receivers =
    {
        ("rx-a", new Point3D(0, 0, 1)), ("rx-b", new Point3D(7, 0, 1)), ("rx-c", new Point3D(0, 6, 1)), ("rx-d", new Point3D(7, 6, 1)),
    };

    private static readonly Point3D[] AnchorSpots =
        { new(2, 2, 0.5), new(5, 1.5, 0.5), new(1.5, 4.5, 0.5), new(5.5, 4.5, 0.5), new(3.5, 3, 0.5) };

    private const double Absorption = 2.7;
    private static double Hot(string rx) => rx == "rx-b" ? 6 : 0;

    /// <param name="usableAnchorRef">False gives the anchors no usable rssi@1m (0): the node-link score leaves them out, the spots do not.</param>
    private static OptimizationSnapshot Snap(DateTime t, int anchors, bool usableAnchorRef = true)
    {
        var nodes = Receivers.Select(r => new OptNode { Id = r.Id, Location = r.At }).ToList();
        var s = new OptimizationSnapshot { Timestamp = t };
        foreach (var tx in nodes)
            foreach (var rx in nodes.Where(n => n != tx))
                s.Measures.Add(new Measure { Tx = tx, Rx = rx, RefRssi = -59, RssiRxAdj = 0,
                    Rssi = -59 + Hot(rx.Id) - 10 * Absorption * Math.Log10(tx.Location.DistanceTo(rx.Location)) });
        for (var i = 0; i < anchors; i++)
        {
            var anchor = new OptNode { Id = $"anchor-{i}", Location = AnchorSpots[i], IsAnchor = true };
            foreach (var rx in nodes)
                s.Measures.Add(new Measure { Tx = anchor, Rx = rx, RefRssi = usableAnchorRef ? -62 - i : 0, RssiRxAdj = 0,
                    Rssi = -62 - i + Hot(rx.Id) - 10 * Absorption * Math.Log10(anchor.Location.DistanceTo(rx.Location)) });
        }
        return s;
    }

    private static OptimizationCycleResult Run(IOptimizer optimizer, int anchors, string judge = "spots", bool usableAnchorRef = true)
    {
        var t0 = DateTime.UtcNow;
        OptimizationSnapshot At(int min) => Snap(t0.AddMinutes(min), anchors, usableAnchorRef);
        var held = new List<OptimizationSnapshot> { At(-2), At(-1), At(0) };
        return OptimizationCycle.Evaluate(At(-10), held, new ConfigOptimization { Enabled = true, Judge = judge },
            new List<IOptimizer> { optimizer }, id => new NodeSettings(id), dryRun: true, t0);
    }

    /// <summary>Sets rxAdj per receiver (absorption as generated); optionally every node's txRef too.</summary>
    private sealed class Propose(string name, Func<string, double> rxAdj, double? txRef = null) : IOptimizer
    {
        public string Name => name;

        public OptimizationResults Optimize(OptimizationSnapshot os, Dictionary<string, NodeSettings> existingSettings)
        {
            var results = new OptimizationResults();
            foreach (var (id, _) in Receivers)
                results.Nodes[id] = new ProposedValues { RxAdjRssi = rxAdj(id), Absorption = Absorption, TxRefRssi = txRef };
            return results;
        }
    }

    private static readonly IOptimizer FixRxB = new Propose("FixRxB", Hot);

    [Test]
    public void FixingTheHotReceiver_IsSelected_OnTheSpots()
    {
        var result = Run(FixRxB, anchors: 5);

        var p = result.Report.Proposals.Single();
        Assert.That(p.Decision, Is.EqualTo(OptimizerGateDecision.Selected));
        var gate = p.Gates.Spots!;
        Assert.That((gate.Spots, gate.Better, gate.Worse, gate.Passed), Is.EqualTo((5, 5, 0, true)));
        Assert.That(gate.GainDb, Is.GreaterThan(2));
        Assert.That(gate.LinkLossDb, Is.LessThan(0), "the node links improve too: the offset is the receiver's");
        Assert.That(gate.GaugeShiftDb, Is.EqualTo(1.5), "rx-b +6 over four receivers");
        Assert.That(result.Selected?.Name, Is.EqualTo("FixRxB"));
    }

    [Test]
    public void TooFewSpots_Holds_AndSaysHowManyAreNeeded()
    {
        var result = Run(FixRxB, anchors: 3);

        Assert.That(result.Report.Proposals.Single().Decision, Is.EqualTo(OptimizerGateDecision.NotEnoughSpots));
        Assert.That(result.Selected, Is.Null);
        Assert.That(result.Report.Outcome, Does.StartWith("Holding:").And.Contain("at least 4").And.Contain("there are 3"));
    }

    [Test]
    public void BreakingAnotherReceiver_IsNotBetterOnTheSpots()
    {
        // Fixes rx-b but puts rx-c 9 dB out: one receiver further out than before, on every spot.
        var result = Run(new Propose("FixBBreakC", id => Hot(id) + (id == "rx-c" ? 9 : 0)), anchors: 5);

        Assert.That(result.Report.Proposals.Single().Decision, Is.EqualTo(OptimizerGateDecision.SpotsNotBetter));
    }

    [Test]
    public void ACommonShiftRidingAlong_IsRefused_EvenThoughSpotsAndLinksCannotSeeIt()
    {
        // The fix plus every rxAdj 5 dB higher and every txRef 5 dB lower (a higher rxAdj lowers the adjusted RSSI, so this
        // pair cancels). Node-to-node links cannot see it, and spots fit their level.
        // Anchors with a usable rssi@1m would still catch it on the links (they hold the gauge); these have none, so
        // only the gauge guard stops the 6.5 dB mean rxAdj shift every device would see as range bias.
        var result = Run(new Propose("FixPlusGauge", id => Hot(id) + 5, txRef: -59 - 5), anchors: 5, usableAnchorRef: false);

        var p = result.Report.Proposals.Single();
        Assert.That(p.Gates.Spots!.GaugeShiftDb, Is.EqualTo(6.5));
        Assert.That(p.Decision, Is.EqualTo(OptimizerGateDecision.GaugeShift));
    }

    /// <summary>
    /// The sign test, not a fixed gain per cycle: a capped step that is better on most spots passes even when it gains
    /// little (the first soak's case: 37 of 52 better, +0.23 dB), and a large gain on too few or mixed spots does not.
    /// </summary>
    [TestCase(52, 37, 15, 0.23, true, TestName = "Consistent small step (soak case) passes")]
    [TestCase(4, 4, 0, 0.8, true, TestName = "4 of 4 spots passes")]
    [TestCase(4, 3, 1, 1.5, false, TestName = "3 of 4 spots is not enough evidence")]
    [TestCase(52, 30, 22, 0.6, false, TestName = "Big but inconsistent gain fails")]
    [TestCase(52, 45, 7, 0.05, false, TestName = "Consistent but negligible gain fails")]
    public void SpotJudge_UsesTheSignTest(int spots, int better, int worse, double gainDb, bool passes)
    {
        var baseline = new OptimizerSpotScore(spots, spots * 8, 5.0);
        var proposal = new OptimizerSpotScore(spots, spots * 8, 5.0 - gainDb) { Better = better, Worse = worse };
        var score = new OptimizerScore(0.5, 0.8, 5.5, 400);

        var gate = OptimizationCycle.SpotGate(proposal, baseline, score, score, new OptimizationResults(), id => new NodeSettings(id));

        Assert.That(gate.SignZ, Is.EqualTo((better - worse) / Math.Sqrt(better + worse)).Within(1e-9));
        Assert.That(gate.Passed, Is.EqualTo(passes));
    }

    [Test]
    public void TheLinksJudge_IsStillTheDefault_AndReportsNoSpotGate()
    {
        Assert.That(new ConfigOptimization().JudgeOnSpots, Is.False);

        var p = Run(FixRxB, anchors: 5, judge: "links").Report.Proposals.Single();

        Assert.That(p.Gates.Spots, Is.Null);
        Assert.That(p.Spots, Is.Not.Null, "the spot score is still reported");
        Assert.That(p.Decision, Is.Not.EqualTo(OptimizerGateDecision.NotEnoughSpots));
    }
}
