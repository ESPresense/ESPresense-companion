using Microsoft.Extensions.Logging.Abstractions;
using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// How much each link counts in the per-node fit. Weights were 1 / rssiVar (floored at 0.1), and nodes report
/// rssiVar from 0.07 to 500 dB² (median about 6), so on a real house the 5% steadiest links carried a third of the
/// fit. A steady reading is not an unbiased one: a link through a wall reads steadily and wrong. Replaying a house
/// with anchored devices held out, that weighting made the fit worse than the calibration it was meant to replace,
/// and the gate rightly refused it every cycle; with the link's own error added to its variance, the fit was
/// accepted and anchors were located about 1 m closer.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PerNodeLinkWeightTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private string _configDir = null!;

    /// <summary>A fresh State whose config enables optimization, so the optimizer reads its default limits.</summary>
    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));
        _state.Apply(new Config { Optimization = new ConfigOptimization { Enabled = true } });
    }

    /// <summary>Stops the config loader and removes the test's config directory.</summary>
    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static readonly (string Id, double X, double Y, double Z)[] Layout =
    {
        ("n01", 0.5, 0.5, 0.5), ("n02", 7.5, 0.6, 1.0), ("n03", 14.5, 0.4, 0.6), ("n04", 0.6, 9.5, 1.1), ("n05", 7.4, 9.6, 0.5),
        ("n06", 14.4, 9.4, 0.9), ("n07", 4.0, 5.0, 1.2), ("n08", 11.0, 5.2, 0.4), ("n09", 3.0, 2.5, 3.5), ("n10", 12.0, 7.5, 3.4),
    };

    /// <summary>
    /// Every node hears every other, 20 repeats, rssi = -59 - 30 log10 d + 2 dB of noise, reported rssiVar 6 (the
    /// median a real house reports). Two of n05's links are 10 dB off with <paramref name="badVar"/> as their rssiVar.
    /// </summary>
    private static OptimizationSnapshot Mesh(double badVar)
    {
        var rnd = new Random(5);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var nodes = Layout.ToDictionary(n => n.Id, n => new OptNode { Id = n.Id, Name = n.Id, Location = new Point3D(n.X, n.Y, n.Z) });
        var os = new OptimizationSnapshot();
        for (int repeat = 0; repeat < 20; repeat++)
            foreach (var rx in nodes.Values)
                foreach (var tx in nodes.Values)
                {
                    if (ReferenceEquals(rx, tx)) continue;
                    var d = rx.Location.DistanceTo(tx.Location);
                    var bad = rx.Id == "n05" && tx.Id is "n01" or "n03";
                    os.Measures.Add(new Measure
                    {
                        Rx = rx, Tx = tx, RefRssi = -59, RssiRxAdj = 0, Distance = d,
                        Rssi = -59 - 30 * Math.Log10(d) + 2 * Gauss() + (bad ? 10 : 0),
                        RssiVar = bad ? badVar : 6,
                    });
                }
        return os;
    }

    /// <summary>The rxAdj the per-node optimizer fits for <paramref name="node"/>, starting every node at rxAdj 0 and absorption 3.</summary>
    private double FittedRxAdj(OptimizationSnapshot os, string node)
    {
        var settings = Layout.ToDictionary(n => n.Id, n => new NodeSettings(n.Id) { Calibration = { RxAdjRssi = 0, Absorption = 3 } });
        var results = new PerNodeAbsorptionRxTx(_state, NullLogger<PerNodeAbsorptionRxTx>.Instance).Optimize(os, settings);
        return results.Nodes[node].RxAdjRssi!.Value;
    }

    /// <summary>
    /// Two links out of n05's nine read steadily (rssiVar 0.1) and 10 dB hot. Weighted 1 / rssiVar they outweighed the
    /// other seven sixty to one and pulled n05's rxAdj to +3 dB (Huber and the transmitters' txRef soak up the rest);
    /// now they count about as much as a typical link, and n05 stays within about 1 dB of its true 0.
    /// </summary>
    [Test]
    public void SteadyButWrongLinks_DoNotDragANode()
    {
        var rxAdj = FittedRxAdj(Mesh(badVar: 0.1), "n05");

        TestContext.Out.WriteLine($"n05 rxAdj {rxAdj:0.00} dB (true 0; two of nine links 10 dB hot and steady)");
        Assert.That(Math.Abs(rxAdj), Is.LessThan(2.0));
    }

    /// <summary>The reported variance still counts: the same two links reading noisily (rssiVar 400) barely move n05.</summary>
    [Test]
    public void NoisyLinks_StillCountLess()
    {
        var steady = FittedRxAdj(Mesh(badVar: 0.1), "n05");
        var noisy = FittedRxAdj(Mesh(badVar: 400), "n05");

        TestContext.Out.WriteLine($"n05 rxAdj: steady bad links {steady:0.00} dB, noisy bad links {noisy:0.00} dB");
        Assert.That(Math.Abs(noisy), Is.LessThan(Math.Abs(steady)));
        Assert.That(Math.Abs(noisy), Is.LessThan(0.5));
    }
}
