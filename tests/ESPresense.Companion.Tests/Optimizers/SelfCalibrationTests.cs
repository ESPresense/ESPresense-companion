using System.Text.Json;
using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// Does continuous self-calibration actually work? Each test hands an optimizer measurements
/// with known ground truth and scores the calibration it proposes by the only metric that
/// matters: does applying it make ranging better?
///
/// The synthetic tests build a fake home where every receiver has a known gain error and a known
/// absorption, generate the exact RSSI such a home would produce, and check the optimizer gets
/// the calibration back out. That proves the machinery; it cannot prove field efficacy, because
/// real radio breaks the log-distance model the optimizers assume.
///
/// Field efficacy is the robot-survey test at the bottom: calibrate on half the surveyed
/// positions, then compare held-out range error against the distances the nodes themselves
/// reported. It uses the anonymised survey bundled under TestData/robot-survey (see RobotSurveyTests).
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class SelfCalibrationTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    private const double TxRef = -59;
    private const double RobotZ = 0.30; // the survey beacon rode on top of the vacuum

    /// <summary>A plausible single-floor spread; pairwise distances run ~1.5 m to ~20 m.</summary>
    private static readonly (string Id, double X, double Y, double Z)[] Layout =
    {
        ("n01", 7.5, 5.0, 1.0),
        ("n02", 0.5, 1.0, 0.5),
        ("n03", 6.0, 0.5, 0.4),
        ("n04", 15.0, 3.0, 1.2),
        ("n05", 11.0, 9.0, 0.4),
        ("n06", 3.5, 6.5, 2.0),
        ("n07", 2.0, 15.5, 0.5),
        ("n08", 13.0, 13.0, 1.0),
        ("n09", 4.5, 11.0, 2.5),
        ("n10", 8.5, 12.5, 2.2),
    };

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));

        // ConfigLoader loads asynchronously; the optimizers need Config.Optimization.
        for (var i = 0; i < 200 && _configLoader.Config == null; i++) Thread.Sleep(10);
        Assert.That(_configLoader.Config, Is.Not.Null, "config did not load");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, true); } catch { /* best effort */ }
    }

    private ConfigOptimization Opt => _configLoader.Config!.Optimization;
    private double MidAbsorption => Opt.AbsorptionMin + (Opt.AbsorptionMax - Opt.AbsorptionMin) / 2;

    private static Dictionary<string, OptNode> MakeNodes() =>
        Layout.ToDictionary(l => l.Id, l => new OptNode { Id = l.Id, Name = l.Id, Location = new Point3D(l.X, l.Y, l.Z) });

    private static double Gauss(Random rnd)
    {
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>
    /// RSSI exactly as a home with these per-receiver gain errors and absorptions would produce it:
    /// rssi = txRef + rxBias - 10*absorption*log10(d) + noise. A positive bias is a receiver that
    /// hears hot, which is precisely what RxAdjRssi exists to absorb.
    /// </summary>
    private static OptimizationSnapshot Synth(Dictionary<string, OptNode> nodes,
        Func<string, double> absorption, Func<string, double> rxBias,
        double noiseSd = 0, int seed = 1, int repeats = 1)
    {
        var rnd = new Random(seed);
        var os = new OptimizationSnapshot();
        foreach (var rx in nodes.Values)
            foreach (var tx in nodes.Values)
            {
                if (ReferenceEquals(rx, tx)) continue;
                var d = rx.Location.DistanceTo(tx.Location);
                if (d <= 1.2) continue; // log10(d) ~ 0 carries no slope information
                for (var r = 0; r < repeats; r++)
                    os.Measures.Add(new Measure
                    {
                        Rx = rx,
                        Tx = tx,
                        Rssi = TxRef + rxBias(rx.Id) - 10 * absorption(rx.Id) * Math.Log10(d) + noiseSd * Gauss(rnd),
                        RssiRxAdj = 0,
                        RefRssi = TxRef,
                        Distance = d,
                    });
            }
        return os;
    }

    /// <summary>Distance the system would infer for this measurement under the proposed calibration.</summary>
    private double Predict(Measure m, OptimizationResults? results)
    {
        ProposedValues? rx = null, tx = null;
        results?.Nodes.TryGetValue(m.Rx.Id, out rx);
        results?.Nodes.TryGetValue(m.Tx.Id, out tx);
        var absorption = rx?.Absorption ?? MidAbsorption;
        var rxAdj = rx?.RxAdjRssi ?? 0;
        var txRef = tx?.TxRefRssi ?? m.RefRssi;
        return Math.Pow(10, (txRef + rxAdj - m.Rssi) / (10 * absorption));
    }

    private double MedianRangeError(OptimizationSnapshot os, OptimizationResults? results) =>
        Median(os.Measures.Select(m => Math.Abs(Predict(m, results) - m.Rx.Location.DistanceTo(m.Tx.Location))));

    private static double Median(IEnumerable<double> xs)
    {
        var v = xs.OrderBy(x => x).ToList();
        Assert.That(v, Is.Not.Empty);
        return v[v.Count / 2];
    }

    /// <summary>
    /// Noise-free sanity: when the world matches the log-distance model exactly, isotonic
    /// regression must hand back the exact per-receiver absorption and gain error it was shown.
    /// An optimizer that cannot pass this is not calibrating, whatever else it does.
    /// </summary>
    [Test]
    public void Isotonic_NoiseFree_RecoversAbsorptionAndRxBiasExactly()
    {
        var nodes = MakeNodes();
        var bias = new Dictionary<string, double>
        {
            ["n01"] = 0, ["n02"] = 6, ["n03"] = -4, ["n04"] = 12, ["n05"] = 18,
            ["n06"] = 3, ["n07"] = 9, ["n08"] = -2, ["n09"] = 15, ["n10"] = 1,
        };
        var os = Synth(nodes, _ => 3.0, id => bias[id]);

        var results = new IsotonicRegressionOptimizer(_state).Optimize(os, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes.Keys, Is.EquivalentTo(nodes.Keys), "every receiver should get a proposal");
        foreach (var (id, p) in results.Nodes)
        {
            Assert.That(p.Absorption, Is.EqualTo(3.0).Within(0.05), $"{id} absorption");
            Assert.That(p.RxAdjRssi, Is.EqualTo(bias[id]).Within(0.5), $"{id} rxAdj");
        }
    }

    /// <summary>
    /// The real question: with 2 dB of radio noise on every reading, does applying the proposed
    /// calibration make inferred distances better than the uncalibrated defaults? The injected
    /// gain errors reach 18 dB, which uncalibrated turns into ~4x range error — calibration
    /// has to claw most of that back even though no single reading is trustworthy.
    /// </summary>
    [Test]
    public void Isotonic_NoisyRadio_CalibrationImprovesRanging()
    {
        var nodes = MakeNodes();
        var bias = new Dictionary<string, double>
        {
            ["n01"] = 0, ["n02"] = 6, ["n03"] = -4, ["n04"] = 12, ["n05"] = 18,
            ["n06"] = 3, ["n07"] = 9, ["n08"] = -2, ["n09"] = 15, ["n10"] = 1,
        };
        var os = Synth(nodes, _ => 3.0, id => bias[id], noiseSd: 2.0, seed: 42, repeats: 3);

        var results = new IsotonicRegressionOptimizer(_state).Optimize(os, new Dictionary<string, NodeSettings>());

        var before = MedianRangeError(os, null);
        var after = MedianRangeError(os, results);
        TestContext.WriteLine($"median range error: uncalibrated {before:0.00} m -> calibrated {after:0.00} m");
        Assert.That(after, Is.LessThan(before / 2), "calibration should at least halve the median range error");

        // The gain estimate is an intercept extrapolated to 1 m; receivers in far corners see a
        // narrow band of distances, so 2 dB of noise legitimately moves it a few dB.
        foreach (var (id, p) in results.Nodes)
            Assert.That(p.RxAdjRssi, Is.EqualTo(bias[id]).Within(6.0), $"{id} rxAdj drifted from the injected gain error");
    }

    /// <summary>
    /// A hopeless home — absorption well above the configured ceiling, one receiver 40 dB hot —
    /// must produce clamped proposals, not runaway ones. The clamps are what make continuous
    /// calibration safe to leave on: a bad week of data can only push settings to the fence.
    /// </summary>
    [Test]
    public void Isotonic_ProposalsStayWithinConfiguredLimits()
    {
        var nodes = MakeNodes();
        var os = Synth(nodes, _ => 6.0, id => id == "n05" ? 40 : 0, noiseSd: 1.0, seed: 7);

        var results = new IsotonicRegressionOptimizer(_state).Optimize(os, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Not.Empty);
        foreach (var (id, p) in results.Nodes)
        {
            Assert.That(p.Absorption, Is.InRange(Opt.AbsorptionMin, Opt.AbsorptionMax), $"{id} absorption");
            Assert.That(p.RxAdjRssi, Is.InRange(Opt.RxAdjRssiMin, Opt.RxAdjRssiMax), $"{id} rxAdj");
        }
        Assert.That(results.Nodes["n05"].RxAdjRssi, Is.EqualTo(Opt.RxAdjRssiMax).Within(1e-6),
            "a 40 dB hot receiver should pin its proposal to the configured maximum");
    }

    /// <summary>
    /// RxAdjRssiOptimizer assumes the mid-limits absorption and a -59 reference, and fits only the
    /// per-receiver gain. Generate a world matching those assumptions and it must find each bias.
    /// </summary>
    [Test]
    public void RxAdj_RecoversPerReceiverBias()
    {
        var nodes = MakeNodes();
        var bias = new Dictionary<string, double>
        {
            ["n01"] = 0, ["n02"] = 5, ["n03"] = -3, ["n04"] = 10, ["n05"] = 16,
            ["n06"] = 2, ["n07"] = 8, ["n08"] = -1, ["n09"] = 13, ["n10"] = 4,
        };
        var mid = MidAbsorption;
        var os = Synth(nodes, _ => mid, id => bias[id], noiseSd: 1.0, seed: 11, repeats: 3);

        var results = new RxAdjRssiOptimizer(_state).Optimize(os, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes.Keys, Is.EquivalentTo(nodes.Keys));
        foreach (var (id, p) in results.Nodes)
            Assert.That(p.RxAdjRssi, Is.EqualTo(bias[id]).Within(2.0), $"{id} rxAdj");
    }

    /// <summary>
    /// per_node_absorption is what config.example.yaml ships, so it is what most users' continuous
    /// calibration actually runs. It fits rxAdj+absorption per receiver and txRef per transmitter
    /// jointly, which leaves a gauge freedom (txRef up, rxAdj down); so instead of checking the
    /// parameters, check the thing users feel — the calibrated system must range better.
    /// </summary>
    [Test]
    public void PerNodeAbsorptionRxTx_JointFit_ImprovesRanging()
    {
        var nodes = MakeNodes();
        var bias = new Dictionary<string, double>
        {
            ["n01"] = 0, ["n02"] = 5, ["n03"] = -3, ["n04"] = 10, ["n05"] = 16,
            ["n06"] = 2, ["n07"] = 8, ["n08"] = -1, ["n09"] = 13, ["n10"] = 4,
        };
        var os = Synth(nodes, _ => 3.0, id => bias[id], noiseSd: 1.0, seed: 23);

        var results = new PerNodeAbsorptionRxTx(_state).Optimize(os, new Dictionary<string, NodeSettings>());

        var before = MedianRangeError(os, null);
        var after = MedianRangeError(os, results);
        TestContext.WriteLine($"median range error: uncalibrated {before:0.00} m -> calibrated {after:0.00} m");
        Assert.That(results.Nodes, Is.Not.Empty, "joint fit proposed nothing");
        Assert.That(after, Is.LessThan(before / 2), "calibration should at least halve the median range error");
    }

    /// <summary>
    /// Field safety on real radio, through the pipeline as shipped: a full optimization cycle —
    /// optimizers plus the acceptance gate in <see cref="OptimizationRunner.RunCycleAsync"/> —
    /// runs on half the robot-surveyed positions, and whatever calibration it chose to apply is
    /// scored on the held-out half. The invariant is non-degradation: leaving continuous
    /// calibration on must not make ranging worse than the settings it started from. The robot
    /// plays a surveyed transmitter: its LiDAR position (floorX/floorY, geometry-fitted frame,
    /// ~0.2 m) is the kind of truth node-to-node calibration enjoys from user-entered node
    /// coordinates.
    ///
    /// On the survey this was developed against, the two modes split sharply. legacy (the code default) works:
    /// its accepted calibration cut held-out median range error from 8.62 m (cold-start defaults)
    /// to 2.26 m, beating even the nodes' own tuned firmware estimates (4.94 m). per_node_absorption
    /// (the config.example.yaml default) is a KNOWN DEFECT, pinned here at its current degradation
    /// so it cannot silently worsen: its quartic penalty against under-estimated distances pushes
    /// rxAdj positive (inflationary), and the gate scores proposals in RSSI space — where Pearson
    /// correlation is blind to that constant shift — so it accepts calibration that is worse in the
    /// metres the locators actually consume.
    /// </summary>
    [TestCase("legacy", 1.10)]
    [TestCase("per_node_absorption", 1.30)]
    public async Task RobotSurvey_HeldOut_CalibrationCycleDoesNotDegradeRanging(string mode, double maxDegradation)
    {
        var survey = JsonSerializer.Deserialize<Survey>(
            ESPresense.Companion.Tests.Locators.RobotSurveyTests.LoadFixtureText("robot-survey-spread.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var points = survey.Points.Where(p => p.FloorX != null && p.FloorY != null).OrderBy(p => p.Label).ToList();
        if (points.Count < 12)
            Assert.Ignore($"needs 12+ surveyed positions for a train/holdout split, fixture has {points.Count}");

        var rxNodes = survey.Nodes.ToDictionary(n => n.Id,
            n => new OptNode { Id = n.Id, Name = n.Id, Location = new Point3D(n.X, n.Y, n.Z) });

        // Dwells happen at different times, so different positions land in different snapshots:
        // the optimizers fit the latest (fitOs) while the gate also holds earlier history (evalOs).
        var train = points.Where((_, i) => i % 2 == 0).ToList();
        var test = points.Where((_, i) => i % 2 == 1).ToList();

        // One physical beacon, one tx identity — exactly one txRef for the joint fit to assign.
        OptimizationSnapshot Snapshot(IEnumerable<SurveyPoint> pts)
        {
            var snap = new OptimizationSnapshot();
            foreach (var p in pts)
                foreach (var (id, meas) in p.M)
                {
                    if (!rxNodes.TryGetValue(id, out var rx)) continue;
                    snap.Measures.Add(new Measure
                    {
                        Rx = rx,
                        Tx = new OptNode { Id = "robot", Name = "robot", Location = new Point3D(p.FloorX!.Value, p.FloorY!.Value, RobotZ) },
                        Rssi = meas.Rssi,
                        RssiRxAdj = 0, // null would hide rxAdj proposals from the gate's evaluation
                        RefRssi = TxRef, // the calibrate beacon's rssi@1m
                        DistVar = meas.Sd * meas.Sd,
                        Distance = meas.Dist,
                    });
                }
            return snap;
        }

        var fitOs = Snapshot(train.Where((_, i) => i % 2 == 0));
        var evalOs = Snapshot(train.Where((_, i) => i % 2 == 1));

        var mqtt = new Mock<IMqttCoordinator>();
        mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var nss = new NodeSettingsStore(mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var runner = new OptimizationRunner(_state, nss,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OptimizationRunner>.Instance,
            _configLoader, Mock.Of<ILeaseService>());

        IList<IOptimizer> optimizers = mode == "per_node_absorption"
            ? new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state) }
            : new List<IOptimizer>
            {
                new RxAdjRssiOptimizer(_state),
                new AbsorptionAvgOptimizer(_state),
                new AbsorptionErrOptimizer(_state),
                new IsotonicRegressionOptimizer(_state),
            };

        // Held-out range error under whatever settings the store currently holds; same distance
        // model OptimizationResults.Evaluate uses, with the beacon's txRef fixed at its rssi@1m.
        double HeldOutMedian()
        {
            var errs = new List<double>();
            foreach (var pt in test)
                foreach (var (id, meas) in pt.M)
                {
                    if (!rxNodes.TryGetValue(id, out var rx)) continue;
                    var s = nss.Get(id).Calibration;
                    var pred = Math.Pow(10, (TxRef + (s.RxAdjRssi ?? 0) - meas.Rssi) / (10 * (s.Absorption ?? 2.7)));
                    errs.Add(Math.Abs(pred - rx.Location.DistanceTo(new Point3D(pt.FloorX!.Value, pt.FloorY!.Value, RobotZ))));
                }
            return Median(errs);
        }

        var reported = Median(test.SelectMany(pt => pt.M
            .Where(kv => rxNodes.ContainsKey(kv.Key))
            .Select(kv => Math.Abs(kv.Value.Dist -
                rxNodes[kv.Key].Location.DistanceTo(new Point3D(pt.FloorX!.Value, pt.FloorY!.Value, RobotZ))))));

        var before = HeldOutMedian();
        await runner.RunCycleAsync(fitOs, new List<OptimizationSnapshot> { evalOs, fitOs }, Opt, optimizers);
        var after = HeldOutMedian();

        var applied = rxNodes.Keys.Where(id => nss.Get(id).Calibration.Absorption != null
                                            || nss.Get(id).Calibration.RxAdjRssi != null).ToList();
        TestContext.WriteLine($"mode {mode}: cycle applied calibration to {applied.Count} receivers ({string.Join(", ", applied)})");
        foreach (var id in applied)
        {
            var c = nss.Get(id).Calibration;
            TestContext.WriteLine($"  {id,-20} absorption {c.Absorption:0.00}  rxAdj {c.RxAdjRssi}");
        }
        TestContext.WriteLine($"\nheld-out median |range error|: before cycle {before:0.00} m, after {after:0.00} m " +
                              $"(nodes' own reported distances: {reported:0.00} m)");

        Assert.That(after, Is.LessThanOrEqualTo(before * maxDegradation),
            $"a calibration cycle ({mode}) degraded held-out ranging: {before:0.00} m -> {after:0.00} m. " +
            "The acceptance gate accepted calibration that does not hold up on data the cycle never saw.");
    }

    private sealed class Survey
    {
        public List<SurveyNode> Nodes { get; set; } = new();
        public List<SurveyPoint> Points { get; set; } = new();
    }

    private sealed class SurveyNode
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    private sealed class SurveyPoint
    {
        public string Label { get; set; } = "";
        public double? FloorX { get; set; }
        public double? FloorY { get; set; }
        public Dictionary<string, Meas> M { get; set; } = new();
    }

    private sealed class Meas
    {
        public double Rssi { get; set; }
        public double Dist { get; set; }
        public double Sd { get; set; }
    }
}
