using Microsoft.Extensions.Logging.Abstractions;
using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// Node-to-node data has a gauge freedom: residual = rssi(rxAdj) - txRef + 10*absorption*log10(d)
/// is unchanged when every receiver's rxAdj goes up k and every node's txRef goes down k (rssi is
/// published as raw - rxAdj, so "every transmitter k dB hot" and "every receiver k dB hot" look the
/// same). Anchored devices break it, because a device is ranged with its own rssi@1m and nothing
/// changes that: their txRef is held, not fitted. These tests build a home with known per-receiver
/// gain errors and a few anchors whose rssi@1m (-65) differs from the nodes' (-59), so holding an
/// anchor anywhere but its rssi@1m shows up as a wrong rxAdj.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class AnchorGaugeTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    private const double NodeTxRef = -59;
    private const double AnchorRefRssi = -65;
    private const double Absorption = 3.0;

    private static readonly (string Id, double X, double Y, double Z)[] NodeLayout =
    {
        ("n01", 7.5, 5.0, 1.0),
        ("n02", 0.5, 1.0, 0.5),
        ("n03", 6.0, 0.5, 0.4),
        ("n04", 15.0, 3.0, 1.2),
        ("n05", 11.0, 9.0, 0.4),
        ("n06", 3.5, 6.5, 2.0),
        ("n07", 2.0, 15.5, 0.5),
        ("n08", 13.0, 13.0, 1.0),
    };

    private static readonly (string Id, double X, double Y, double Z)[] AnchorLayout =
    {
        ("a1", 4.0, 3.0, 0.8),
        ("a2", 10.0, 11.0, 1.1),
        ("a3", 14.0, 6.5, 0.3),
    };

    /// <summary>Per-receiver gain error: what rxAdj should come out as. All inside the default -5..30 limits even shifted by the gauge.</summary>
    private static readonly Dictionary<string, double> Bias = new()
    {
        ["n01"] = 4, ["n02"] = 9, ["n03"] = 2, ["n04"] = 12,
        ["n05"] = 7, ["n06"] = 3, ["n07"] = 10, ["n08"] = 5,
    };

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));
        _state.Apply(new Config { Optimization = new ConfigOptimization { Enabled = true } });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private static OptNode Node((string Id, double X, double Y, double Z) l, bool anchor) =>
        new() { Id = l.Id, Name = l.Id, Location = new Point3D(l.X, l.Y, l.Z), IsAnchor = anchor };

    /// <summary>
    /// Exact RSSI for this home, published the way the firmware does (raw - rxAdj, with rxAdj 0 at
    /// capture): rssi = txRef + bias[rx] - 10*absorption*log10(d).
    /// </summary>
    private static OptimizationSnapshot Synth(bool nodeToNode = true, bool anchors = true)
    {
        var nodes = NodeLayout.Select(l => Node(l, false)).ToList();
        var os = new OptimizationSnapshot();
        void Add(OptNode tx, OptNode rx, double txRef)
        {
            var d = rx.Location.DistanceTo(tx.Location);
            os.Measures.Add(new Measure
            {
                Rx = rx,
                Tx = tx,
                Rssi = txRef + Bias[rx.Id] - 10 * Absorption * Math.Log10(d),
                RssiRxAdj = 0,
                RefRssi = txRef,
                Distance = d,
            });
        }

        if (nodeToNode)
            foreach (var tx in nodes)
                foreach (var rx in nodes.Where(rx => !ReferenceEquals(rx, tx)))
                    Add(tx, rx, NodeTxRef);
        if (anchors)
            foreach (var anchor in AnchorLayout.Select(l => Node(l, true)))
                foreach (var rx in nodes)
                    Add(anchor, rx, AnchorRefRssi);
        return os;
    }

    private static string[] NodeIds => NodeLayout.Select(l => l.Id).ToArray();
    private static string[] AnchorIds => AnchorLayout.Select(l => l.Id).ToArray();

    /// <summary>The true calibration, optionally moved k dB along the gauge: rxAdj + k, every node's txRef - k.</summary>
    private static OptimizationResults Calibration(double k = 0)
    {
        var or = new OptimizationResults();
        foreach (var id in NodeIds)
            or.Nodes[id] = new ProposedValues { Absorption = Absorption, RxAdjRssi = Bias[id] + k, TxRefRssi = NodeTxRef - k };
        return or;
    }

    private static NodeSettings NoSettings(string id) => new(id);

    private IOptimizer Make() => new PerNodeAbsorptionRxTx(_state, NullLogger<PerNodeAbsorptionRxTx>.Instance);

    /// <summary>
    /// The gauge itself, in the score the runner uses: without anchors, moving the calibration along
    /// it leaves node-to-node RMSE exactly where it was, so no data can choose; with anchors held at
    /// their rssi@1m, the same move costs the anchors' links k dB each.
    /// </summary>
    [Test]
    public void Evaluate_GaugeShift_IsInvisibleNodeToNode_ButNotWithAnchors()
    {
        const double k = 5;

        var nodesOnly = new List<OptimizationSnapshot> { Synth(anchors: false) };
        var trueNodesOnly = Calibration().Evaluate(nodesOnly, NoSettings);
        var shiftedNodesOnly = Calibration(k).Evaluate(nodesOnly, NoSettings);
        Assert.That(trueNodesOnly.RMSE, Is.EqualTo(0).Within(1e-9));
        Assert.That(shiftedNodesOnly.RMSE, Is.EqualTo(trueNodesOnly.RMSE).Within(1e-9),
            "node-to-node data alone cannot see a shift along the gauge");

        var withAnchors = new List<OptimizationSnapshot> { Synth() };
        var trueWithAnchors = Calibration().Evaluate(withAnchors, NoSettings);
        var shiftedWithAnchors = Calibration(k).Evaluate(withAnchors, NoSettings);
        Assert.That(trueWithAnchors.RMSE, Is.EqualTo(0).Within(1e-9));
        Assert.That(shiftedWithAnchors.RMSE, Is.GreaterThan(1.0), "anchors held at rssi@1m must make the gauge shift cost something");
    }

    /// <summary>
    /// An anchor is scored with the rssi@1m it was heard with in both frames, whatever a proposal or
    /// the settings store says about its txRef (before this fix the runner could apply one to the
    /// device's id, and the device frame would then score the anchor at that stale value or -59).
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void Evaluate_Anchor_IsScoredAtItsRefRssi(bool deviceFrame)
    {
        var anchorsOnly = new List<OptimizationSnapshot> { Synth(nodeToNode: false) };
        var proposal = Calibration();
        foreach (var id in AnchorIds) proposal.Nodes[id] = new ProposedValues { TxRefRssi = -50 };
        NodeSettings Stale(string id)
        {
            var ns = new NodeSettings(id);
            if (AnchorIds.Contains(id)) ns.Calibration.TxRefRssi = -50;
            return ns;
        }

        var eval = proposal.Evaluate(anchorsOnly, Stale, deviceFrame);

        Assert.That(eval.Samples, Is.EqualTo(AnchorIds.Length * NodeIds.Length));
        Assert.That(eval.RMSE, Is.EqualTo(0).Within(1e-9), "the anchor should be predicted from its rssi@1m, not a txRef from a proposal or the store");
    }

    /// <summary>
    /// The fit itself. The incumbent has drifted 6 dB along the gauge (every node's txRef configured
    /// at -53 instead of -59), which node-to-node data cannot see, so without anchors the txRef prior
    /// keeps the fit on the drifted side of the ridge and every rxAdj comes out ~6 dB low. With
    /// anchors, whose txRef is held at their rssi@1m, the ridge is gone: rxAdj lands on the true
    /// gain errors and the node txRefs return to -59. (For per_node_absorption this also needs the
    /// txRef prior to let go of the common shift when anchors are present; held at the drifted
    /// incumbent it pulled rxAdj 2.5 dB back toward it.)
    /// </summary>
    [Test]
    public void Optimize_AnchorsPinTheGauge()
    {
        const double drift = 6;
        Dictionary<string, NodeSettings> Drifted() => NodeIds.ToDictionary(id => id, id =>
        {
            var ns = new NodeSettings(id);
            ns.Calibration.TxRefRssi = (int)(NodeTxRef + drift);
            ns.Calibration.RxAdjRssi = (int)(Bias[id] - drift);
            ns.Calibration.Absorption = Absorption;
            return ns;
        });

        double MeanRxAdjError(OptimizationResults r) => NodeIds.Average(id => r.Nodes[id].RxAdjRssi!.Value - Bias[id]);

        var free = Make().Optimize(Synth(anchors: false), Drifted());
        var pinned = Make().Optimize(Synth(), Drifted());

        var freeError = MeanRxAdjError(free);
        var pinnedError = MeanRxAdjError(pinned);
        TestContext.WriteLine($"mean rxAdj error without anchors {freeError:0.00} dB, with anchors {pinnedError:0.00} dB");

        Assert.That(Math.Abs(freeError), Is.GreaterThan(2), "sanity: without anchors the fit should sit on the drifted side of the gauge");
        Assert.That(pinnedError, Is.EqualTo(0).Within(0.5), "anchors should place rxAdj in absolute terms");
        foreach (var id in NodeIds)
        {
            Assert.That(pinned.Nodes[id].RxAdjRssi, Is.EqualTo(Bias[id]).Within(1.0), $"{id} rxAdj");
            Assert.That(pinned.Nodes[id].TxRefRssi, Is.EqualTo(NodeTxRef).Within(1.0), $"{id} txRef");
        }
    }

    /// <summary>
    /// An anchored device's txRef is not a parameter: no proposal names it, so the runner never
    /// publishes calibration to a device id, and the value it is held at is its rssi@1m. With anchors
    /// only, that is visible directly: holding them anywhere but -65 would push every rxAdj off by
    /// the difference.
    /// </summary>
    [Test]
    public void Optimize_NeverProposesTxRefForAnAnchor_AndHoldsItAtRefRssi()
    {
        foreach (var os in new[] { Synth(), Synth(nodeToNode: false) })
        {
            var results = Make().Optimize(os, new Dictionary<string, NodeSettings>());

            Assert.That(results.Nodes, Is.Not.Empty);
            foreach (var id in AnchorIds)
                Assert.That(results.Nodes.TryGetValue(id, out var pv) ? pv.TxRefRssi : null, Is.Null, $"{id} is anchored; its txRef must not be proposed");
            foreach (var id in NodeIds)
                Assert.That(results.Nodes[id].RxAdjRssi, Is.EqualTo(Bias[id]).Within(1.0), $"{id} rxAdj with the anchors held at rssi@1m");
        }
    }
}
