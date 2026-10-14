using System.Text.Json;
using ESPresense.Companion.Tests.Locators;
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
    private NodeTelemetryStore _nts = null!;
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
        _nts = new NodeTelemetryStore(mqtt.Object);
        _state = new State(_configLoader, _nts);

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

    // ---- robot survey: real radio, through the pipeline as shipped -------------------------------

    private Survey LoadSpread()
    {
        var survey = JsonSerializer.Deserialize<Survey>(
            RobotSurveyTests.LoadFixtureText("robot-survey-spread.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        survey.Points = survey.Points.Where(p => p.FloorX != null && p.FloorY != null).OrderBy(p => p.Label).ToList();
        if (survey.Points.Count < 12)
            Assert.Ignore($"needs 12+ surveyed positions for a train/holdout split, fixture has {survey.Points.Count}");
        return survey;
    }

    private static Dictionary<string, OptNode> RxNodes(Survey survey) => survey.Nodes.ToDictionary(n => n.Id,
        n => new OptNode { Id = n.Id, Name = n.Id, Location = new Point3D(n.X, n.Y, n.Z) });

    /// <summary>The robot's LiDAR position (floorX/floorY, geometry-fitted frame, ~0.2 m) with the beacon on top.</summary>
    private static Point3D Truth(SurveyPoint p) => new(p.FloorX!.Value, p.FloorY!.Value, RobotZ);

    /// <summary>
    /// One physical beacon, one tx identity — exactly one txRef for the joint fit to assign. The
    /// robot plays a surveyed transmitter: its position is the kind of truth node-to-node
    /// calibration enjoys from user-entered node coordinates.
    /// </summary>
    private static OptimizationSnapshot Snapshot(IEnumerable<SurveyPoint> pts, Dictionary<string, OptNode> rxNodes, Func<Meas, double>? rssi = null)
    {
        var snap = new OptimizationSnapshot();
        foreach (var p in pts)
            foreach (var (id, meas) in p.M)
            {
                if (!rxNodes.TryGetValue(id, out var rx)) continue;
                snap.Measures.Add(new Measure
                {
                    Rx = rx,
                    Tx = new OptNode { Id = "robot", Name = "robot", Location = Truth(p) },
                    Rssi = rssi?.Invoke(meas) ?? meas.Rssi,
                    RssiRxAdj = meas.RxAdj ?? 0, // the rxAdj baked into Rssi; null would hide rxAdj proposals from the gate
                    RefRssi = TxRef, // the calibrate beacon's rssi@1m
                    DistVar = meas.Sd * meas.Sd,
                    Distance = meas.Dist,
                });
            }
        return snap;
    }

    private (OptimizationRunner Runner, NodeSettingsStore Nss) MakeRunner()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        var nss = new NodeSettingsStore(mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var runner = new OptimizationRunner(_state, nss,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OptimizationRunner>.Instance,
            _configLoader, Mock.Of<ILeaseService>());
        return (runner, nss);
    }

    private IList<IOptimizer> MakeOptimizers(string mode) => mode == "per_node_absorption"
        ? new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state) }
        : new List<IOptimizer>
        {
            new RxAdjRssiOptimizer(_state),
            new AbsorptionAvgOptimizer(_state),
            new AbsorptionErrOptimizer(_state),
            new IsotonicRegressionOptimizer(_state),
        };

    /// <summary>
    /// Distance the system would infer under these settings; same model OptimizationResults.Evaluate
    /// uses, with the beacon's txRef fixed at its rssi@1m, exactly as a device is ranged. The node
    /// would publish raw - rxAdj under the new settings, and raw is Rssi + the rxAdj at capture.
    /// </summary>
    private static double Ranged(Meas meas, NodeSettings s) =>
        Math.Pow(10, (TxRef + (s.Calibration.RxAdjRssi ?? 0) - (meas.RxAdj ?? 0) - meas.Rssi) / (10 * (s.Calibration.Absorption ?? 2.7)));

    private static double HeldOutMedian(IEnumerable<SurveyPoint> test, Dictionary<string, OptNode> rxNodes, NodeSettingsStore nss) =>
        Median(test.SelectMany(pt => pt.M
            .Where(kv => rxNodes.ContainsKey(kv.Key))
            .Select(kv => Math.Abs(Ranged(kv.Value, nss.Get(kv.Key)) - rxNodes[kv.Key].Location.DistanceTo(Truth(pt))))));

    private static string Describe(NodeSettingsStore nss, string id)
    {
        var c = nss.Get(id).Calibration;
        return c.Absorption == null && c.RxAdjRssi == null ? "-" : $"{c.Absorption:0.00}/{c.RxAdjRssi,3}";
    }

    /// <summary>
    /// Field efficacy on real radio: a full optimization cycle — optimizers plus the acceptance gate
    /// in <see cref="OptimizationRunner.RunCycleAsync"/> — runs on half the robot-surveyed
    /// positions, and whatever calibration it chose to apply is scored on the held-out half.
    ///
    /// On the survey this was developed against, both modes cut held-out median range error from
    /// 8.62 m (cold-start defaults) to ~2.3 m, well under the nodes' own tuned firmware estimates
    /// (4.94 m); the bar here is half, leaving room for other homes. per_node_absorption (the
    /// config.example.yaml default) used to make it WORSE (10.65 m): its quartic penalty against
    /// under-estimated distances pushed rxAdj positive, and the joint fit hid that behind a
    /// matching txRef shift the gate's RSSI-space score cannot see. It now fits in dB with txRef
    /// anchored, and the gate also scores with txRef held (see OptimizationResults.Evaluate).
    /// </summary>
    [TestCase("legacy", 0.5)]
    [TestCase("per_node_absorption", 0.5)]
    public async Task RobotSurvey_HeldOut_CalibrationCycleImprovesRanging(string mode, double maxRatio)
    {
        var survey = LoadSpread();
        var rxNodes = RxNodes(survey);

        // Dwells happen at different times, so different positions land in different snapshots:
        // the optimizers fit the latest (fitOs) while the gate also holds earlier history (evalOs).
        var train = survey.Points.Where((_, i) => i % 2 == 0).ToList();
        var test = survey.Points.Where((_, i) => i % 2 == 1).ToList();
        var fitOs = Snapshot(train.Where((_, i) => i % 2 == 0), rxNodes);
        var evalOs = Snapshot(train.Where((_, i) => i % 2 == 1), rxNodes);

        var (runner, nss) = MakeRunner();

        var reported = Median(test.SelectMany(pt => pt.M
            .Where(kv => rxNodes.ContainsKey(kv.Key))
            .Select(kv => Math.Abs(kv.Value.Dist - rxNodes[kv.Key].Location.DistanceTo(Truth(pt))))));

        var before = HeldOutMedian(test, rxNodes, nss);
        await runner.RunCycleAsync(fitOs, new List<OptimizationSnapshot> { evalOs, fitOs }, Opt, MakeOptimizers(mode));
        var after = HeldOutMedian(test, rxNodes, nss);

        var applied = rxNodes.Keys.Where(id => nss.Get(id).Calibration.Absorption != null
                                            || nss.Get(id).Calibration.RxAdjRssi != null).ToList();
        TestContext.WriteLine($"mode {mode}: cycle applied calibration to {applied.Count} receivers (absorption/rxAdj)");
        foreach (var id in applied) TestContext.WriteLine($"  {id,-6} {Describe(nss, id)}");
        TestContext.WriteLine($"\nheld-out median |range error|: before cycle {before:0.00} m, after {after:0.00} m " +
                              $"(nodes' own reported distances: {reported:0.00} m)");

        Assert.That(applied, Is.Not.Empty, $"a calibration cycle ({mode}) applied nothing on cold-start defaults");
        Assert.That(after, Is.LessThanOrEqualTo(before * maxRatio),
            $"a calibration cycle ({mode}) should cut held-out range error to at most {maxRatio:0%} of cold-start defaults: " +
            $"{before:0.00} m -> {after:0.00} m. Either the optimizer fits noise or the gate accepted calibration " +
            "that does not hold up on data the cycle never saw.");
    }

    /// <summary>
    /// Continuous calibration runs every interval on a home that has not changed, so successive
    /// cycles see the same radio plus noise. Proposals must settle, not wander: the live Companion
    /// was seen rewriting a node's rxAdj by 3–7 dB an hour on a stationary house, which is the gate
    /// accepting noise as improvement over and over. Each cycle here gets a fresh noisy snapshot
    /// (per-measurement sd from the survey) and the short history production keeps; after the
    /// first applied calibration the settings must stay put and held-out ranging must not slide.
    /// </summary>
    [TestCase("legacy")]
    [TestCase("per_node_absorption")]
    public async Task RobotSurvey_SuccessiveCycles_SettingsConvergeRatherThanWander(string mode)
    {
        var survey = LoadSpread();
        var rxNodes = RxNodes(survey);
        var train = survey.Points.Where((_, i) => i % 2 == 0).ToList();
        var test = survey.Points.Where((_, i) => i % 2 == 1).ToList();

        var rnd = new Random(3);
        OptimizationSnapshot Noisy() => Snapshot(train, rxNodes, m => m.Rssi + Math.Max(1.0, m.Sd) * Gauss(rnd));

        var (runner, nss) = MakeRunner();
        var optimizers = MakeOptimizers(mode);

        // ExecuteAsync primes three snapshots before the first cycle and keeps a few minutes of history.
        const int cycles = 8, window = 4;
        var history = new List<OptimizationSnapshot> { Noisy(), Noisy(), Noisy() };
        var trajectory = new List<(int Cycle, Dictionary<string, (int? RxAdj, double? Absorption)> Settings, double HeldOut)>();
        for (var k = 0; k < cycles; k++)
        {
            var os = Noisy();
            history.Add(os);
            if (history.Count > window) history.RemoveAt(0);
            await runner.RunCycleAsync(os, history.ToList(), Opt, optimizers);
            trajectory.Add((k,
                rxNodes.Keys.ToDictionary(id => id, id => (nss.Get(id).Calibration.RxAdjRssi, nss.Get(id).Calibration.Absorption)),
                HeldOutMedian(test, rxNodes, nss)));
        }

        var ids = rxNodes.Keys.OrderBy(x => x).ToList();
        TestContext.WriteLine($"mode {mode}: settings (absorption/rxAdj) after each cycle, held-out median range error");
        TestContext.WriteLine($"{"cycle",5} {"m",6}  " + string.Join(" ", ids.Select(id => $"{id,9}")));
        foreach (var t in trajectory)
            TestContext.WriteLine($"{t.Cycle,5} {t.HeldOut,6:0.00}  " + string.Join(" ", ids.Select(id =>
                t.Settings[id] is { RxAdj: null, Absorption: null } ? $"{"-",9}" : $"{t.Settings[id].Absorption,5:0.00}/{t.Settings[id].RxAdj,3}")));

        Assert.That(trajectory.Any(t => t.Settings.Values.Any(s => s.RxAdj != null || s.Absorption != null)), "no cycle applied any calibration");

        // The first couple of cycles may still refine while the history fills; after that the settings
        // must hold. 3 dB / 0.3 are a step of the kind the live Companion was seen taking every hour.
        const int settleBy = 2;
        var settled = trajectory[settleBy];
        var wander = new List<string>();
        foreach (var t in trajectory.Skip(settleBy + 1))
            foreach (var id in ids)
            {
                var (rx0, ab0) = settled.Settings[id];
                var (rx1, ab1) = t.Settings[id];
                if (Math.Abs((rx1 ?? 0) - (rx0 ?? 0)) > 3) wander.Add($"cycle {t.Cycle} {id} rxAdj {rx0} -> {rx1}");
                if (Math.Abs((ab1 ?? 0) - (ab0 ?? 0)) > 0.3) wander.Add($"cycle {t.Cycle} {id} absorption {ab0:0.00} -> {ab1:0.00}");
            }
        Assert.That(wander, Is.Empty, $"settings kept moving after cycle {settleBy} ({mode}):\n  " + string.Join("\n  ", wander));

        // And where it settles has to be good: the same bar as the single clean cycle, now under noise.
        var cold = HeldOutMedian(test, rxNodes, MakeRunner().Nss);
        Assert.That(trajectory[^1].HeldOut, Is.LessThanOrEqualTo(cold * 0.5),
            $"after {cycles} noisy cycles ({mode}) held-out range error is {trajectory[^1].HeldOut:0.00} m against {cold:0.00} m cold-start; " +
            "the gate let noise-fitted calibration through and then protected it");
    }

    /// <summary>
    /// The loop closed: does the calibration a cycle applies make the *position* better, not just
    /// the ranges? Each held-out point is ranged three ways — cold-start defaults, the applied
    /// calibration, and the distances the nodes themselves reported — and each set goes through
    /// the locators. First-floor nodes only, as in RobotSurveyTests; error is XY distance from the
    /// robot's LiDAR position.
    /// </summary>
    [TestCase("legacy")]
    [TestCase("per_node_absorption")]
    public async Task RobotSurvey_HeldOut_CalibrationImprovesPosition(string mode)
    {
        var survey = LoadSpread();
        var rxNodes = RxNodes(survey);
        var train = survey.Points.Where((_, i) => i % 2 == 0).ToList();
        var test = survey.Points.Where((_, i) => i % 2 == 1).ToList();

        var (runner, nss) = MakeRunner();
        await runner.RunCycleAsync(Snapshot(train.Where((_, i) => i % 2 == 0), rxNodes),
            new List<OptimizationSnapshot> { Snapshot(train.Where((_, i) => i % 2 == 1), rxNodes), Snapshot(train.Where((_, i) => i % 2 == 0), rxNodes) },
            Opt, MakeOptimizers(mode));
        var cold = MakeRunner().Nss;

        var harness = new SurveyLocatorHarness(_state, _configLoader, _nts, survey.FloorBounds["first"],
            survey.Nodes.Where(n => n.Floors.Contains("first")).Select(n => (n.Id, n.X, n.Y, n.Z)));

        Dictionary<string, double> Dists(SurveyPoint pt, Func<string, Meas, double> f) =>
            pt.M.Where(kv => harness.Nodes.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => f(kv.Key, kv.Value));

        double MedianXyError(string which, Func<string, Meas, double> f)
        {
            var errs = new List<double>();
            foreach (var pt in test)
            {
                var fix = harness.Solve(which, Dists(pt, f));
                if (fix == null) continue;
                var t = Truth(pt);
                errs.Add(Math.Sqrt(Math.Pow(fix.Value.X - t.X, 2) + Math.Pow(fix.Value.Y - t.Y, 2)));
            }
            return errs.Count == 0 ? double.NaN : Median(errs);
        }

        var locators = new[] { "gaussnewton", "nelder", "mle", "nadaraya", "nearest" };
        TestContext.WriteLine($"mode {mode}: held-out median XY position error (m) by locator");
        TestContext.WriteLine($"{"locator",-12} {"cold",8} {"calibrated",11} {"reported",9}");
        var ratios = new List<double>();
        foreach (var which in locators)
        {
            var c = MedianXyError(which, (_, m) => Ranged(m, cold.Get("none")));
            var k = MedianXyError(which, (id, m) => Ranged(m, nss.Get(id)));
            var r = MedianXyError(which, (_, m) => m.Dist);
            TestContext.WriteLine($"{which,-12} {c,8:0.00} {k,11:0.00} {r,9:0.00}");
            if (double.IsFinite(c) && double.IsFinite(k)) ratios.Add(k / c);
        }

        Assert.That(ratios, Is.Not.Empty, "no locator produced fixes");
        Assert.That(Median(ratios), Is.LessThan(0.8),
            $"the applied calibration ({mode}) should cut held-out position error well below cold-start defaults across locators; median ratio {Median(ratios):0.00}");
    }

    private sealed class Survey
    {
        public List<SurveyNode> Nodes { get; set; } = new();
        public List<SurveyPoint> Points { get; set; } = new();
        public Dictionary<string, double[][]> FloorBounds { get; set; } = new();
    }

    private sealed class SurveyNode
    {
        public string Id { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public List<string> Floors { get; set; } = new();
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
        /// <summary>As published: the node subtracts its rxAdj before reporting (BleFingerprint.cpp), so raw = Rssi + RxAdj.</summary>
        public double Rssi { get; set; }
        public double Dist { get; set; }
        public double Sd { get; set; }
        /// <summary>The node's rxAdj at capture, when the fixture records it; without it proposals are relative to that unknown.</summary>
        public double? RxAdj { get; set; }
    }
}
