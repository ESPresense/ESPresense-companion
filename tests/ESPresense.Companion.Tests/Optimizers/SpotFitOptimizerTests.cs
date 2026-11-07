using ESPresense.Models;
using ESPresense.Optimizers;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>The spot-fit optimizer (#1868): per-receiver rxAdj and absorption fitted on taught spots. All ids are made up.</summary>
public class SpotFitOptimizerTests
{
    private static readonly (string Id, Point3D At)[] Receivers =
    {
        ("rx-a", new Point3D(0, 0, 1)), ("rx-b", new Point3D(8, 0, 1)), ("rx-c", new Point3D(0, 6, 1)), ("rx-d", new Point3D(8, 6, 1)),
    };

    // Limits centred on the true absorption, so the prior does not pull the fit off it.
    private static ConfigOptimization Limits => new()
    {
        Limits = { ["absorption_min"] = 2.5, ["absorption_max"] = 3.3, ["rx_adj_rssi_min"] = -15, ["rx_adj_rssi_max"] = 25 }
    };

    /// <summary>Spots on a grid, raw RSSI from absorption 2.9 with rx-b <paramref name="hot"/> dB hot; each device at its own level.</summary>
    private static List<KnownPositionSpot> Spots(int count, double hot = 6) => Enumerable.Range(0, count).Select(i =>
    {
        var at = new Point3D(1 + (i % 4) * 2, 1 + (i / 4) * 2, 0.4);
        return new KnownPositionSpot($"spot-{i}", at, Receivers.Select(r => new SpotReading(r.Id, r.At,
            -58 - i + (r.Id == "rx-b" ? hot : 0) - 10 * 2.9 * Math.Log10(r.At.DistanceTo(at)))).ToList());
    }).ToList();

    private static Dictionary<string, NodeSettings> Incumbent(int rxAdj) => Receivers.ToDictionary(r => r.Id,
        r => new NodeSettings(r.Id) { Calibration = { RxAdjRssi = rxAdj, Absorption = 2.7 } });

    [Test]
    public void RecoversAHotReceiver_AndTheAbsorption_WithinOneStep()
    {
        // 2 dB hot: rx-b +1.5 and the others -0.5 around the incumbent's mean, under the 3 dB step cap.
        var p = new SpotFitOptimizer(Spots(8, hot: 2), Limits).Optimize(new OptimizationSnapshot(), Incumbent(0)).Nodes;

        var others = new[] { "rx-a", "rx-c", "rx-d" }.Average(id => p[id].RxAdjRssi!.Value);
        Assert.That(p["rx-b"].RxAdjRssi!.Value - others, Is.EqualTo(2).Within(0.05));
        Assert.That(p.Values.Select(v => v.Absorption!.Value), Has.All.EqualTo(2.9).Within(0.02), "2.7 to 2.9 is within the 0.2 step");
    }

    [Test]
    public void ALargeCorrection_IsCappedPerCycle_KeepingItsDirectionAndMean()
    {
        // 6 dB hot wants rx-b +4.5 and the others -1.5: scaled down so the largest step is 3 dB.
        var p = new SpotFitOptimizer(Spots(8), Limits).Optimize(new OptimizationSnapshot(), Incumbent(0)).Nodes;

        Assert.That(p["rx-b"].RxAdjRssi!.Value, Is.EqualTo(SpotFitOptimizer.MaxRxAdjStepDb).Within(0.05));
        Assert.That(p["rx-a"].RxAdjRssi!.Value, Is.EqualTo(-1).Within(0.05));
        Assert.That(p.Values.Average(v => v.RxAdjRssi!.Value), Is.EqualTo(0).Within(1e-6));
    }

    [TestCase(0)]
    [TestCase(5)]
    public void KeepsTheMeanRxAdj_WhereTheIncumbentHadIt(int incumbentRxAdj)
    {
        // Spots cannot see a common shift, so the fit must not place one.
        var p = new SpotFitOptimizer(Spots(8), Limits).Optimize(new OptimizationSnapshot(), Incumbent(incumbentRxAdj)).Nodes;

        Assert.That(p.Values.Average(v => v.RxAdjRssi!.Value), Is.EqualTo(incumbentRxAdj).Within(1e-6));
    }

    [Test]
    public void TooFewSpots_ProposeNothing()
    {
        Assert.That(new SpotFitOptimizer(Spots(SpotFitOptimizer.MinSpots - 1), Limits).Optimize(new OptimizationSnapshot(), Incumbent(0)).Nodes, Is.Empty);
    }

    [Test]
    public void AReceiverHeardInTooFewSpots_GetsNoValue()
    {
        var spots = Spots(6);
        var extra = new Point3D(20, 20, 1);
        for (var i = 0; i < 2; i++)
            spots[i] = spots[i] with { Readings = spots[i].Readings.Append(new SpotReading("rx-far", extra, -90)).ToList() };

        var p = new SpotFitOptimizer(spots, Limits).Optimize(new OptimizationSnapshot(), Incumbent(0)).Nodes;

        Assert.That(p.Keys, Is.EquivalentTo(Receivers.Select(r => r.Id)));
    }

    [Test]
    public void TheSplit_IsStable_AndKeepsAboutAThirdForJudging()
    {
        var spots = Enumerable.Range(0, 300).Select(i => new KnownPositionSpot($"taught:f1:{i * 0.25:0.00},1.00", Point3D.Origin, Array.Empty<SpotReading>())).ToList();

        var (fit, judge) = SpotFitOptimizer.SplitForJudging(spots);
        var (fit2, judge2) = SpotFitOptimizer.SplitForJudging(spots.AsEnumerable().Reverse());

        Assert.That(fit.Count + judge.Count, Is.EqualTo(300));
        Assert.That(judge.Count, Is.InRange(75, 125));
        Assert.That(judge2.Select(s => s.Id), Is.EquivalentTo(judge.Select(s => s.Id)), "a spot stays on its side whatever the order");
    }

    [Test]
    public void FittedOnOneShare_IsSelectedOnTheOther()
    {
        var spots = Spots(12);
        var (fit, judge) = SpotFitOptimizer.SplitForJudging(spots);
        Assume.That(fit.Count, Is.GreaterThanOrEqualTo(SpotFitOptimizer.MinSpots));
        Assume.That(judge.Count, Is.GreaterThanOrEqualTo(OptimizationCycle.SpotGateMinSpots));

        // Node-to-node links that the incumbent and the proposal score alike: two nodes heard by each other only.
        var n1 = new OptNode { Id = "rx-a", Location = Receivers[0].At }; var n2 = new OptNode { Id = "rx-c", Location = Receivers[2].At };
        OptimizationSnapshot Snap(DateTime t) => new() { Timestamp = t, Measures =
        {
            new Measure { Tx = n1, Rx = n2, RefRssi = -59, RssiRxAdj = 0, Rssi = -59 - 10 * 2.9 * Math.Log10(6) },
            new Measure { Tx = n2, Rx = n1, RefRssi = -59, RssiRxAdj = 0, Rssi = -59 - 10 * 2.9 * Math.Log10(6) },
        } };
        var t0 = DateTime.UtcNow;
        var opt = Limits; opt.Enabled = true; opt.Judge = "spots"; opt.MinSamples = 2;

        var result = OptimizationCycle.Evaluate(Snap(t0.AddMinutes(-10)), new List<OptimizationSnapshot> { Snap(t0.AddMinutes(-1)), Snap(t0) }, opt,
            new List<IOptimizer> { new SpotFitOptimizer(fit, opt) }, id => Incumbent(0)[id], dryRun: true, t0, knownSpots: judge);

        var p = result.Report.Proposals.Single();
        Assert.That(p.Gates.Spots!.Spots, Is.EqualTo(judge.Count), "judged only on the spots it did not fit");
        Assert.That(p.Decision, Is.EqualTo(OptimizerGateDecision.Selected), p.Gates.Spots.ToString());
    }
}
