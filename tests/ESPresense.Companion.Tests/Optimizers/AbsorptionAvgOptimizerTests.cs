using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class AbsorptionAvgOptimizerTests
{
    private State _state = null!;
    private ConfigLoader _configLoader = null!;
    private NodeTelemetryStore _nodeTelemetryStore = null!;
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();

        if (Directory.Exists(_configDir))
        {
            try
            {
                Directory.Delete(_configDir, recursive: true);
            }
            catch (IOException)
            {
                // Ignore cleanup errors
            }
        }
    }

    private static OptNode NodeAt(string id, double x) => new() { Id = id, Name = id, Location = new Point3D(x, 0, 0) };

    private static Measure MeasureOf(OptNode rx, OptNode tx, double rssi) => new()
    {
        Rx = rx,
        Tx = tx,
        Rssi = rssi,
        RefRssi = -59,
        Distance = rx.Location.DistanceTo(tx.Location)
    };

    [Test]
    public void Optimize_SkipsMeasurementsAtOrBelowOneMetre()
    {
        var rx = NodeAt("rx", 0);
        var snapshot = new OptimizationSnapshot();
        // d == 1: log10(1) == 0, exponent would be +/-Infinity
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("at-one", 1.0), -100));
        // d < 1: log10(d) < 0, exponent flips sign
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("below-one", 0.5), -80));
        // d == 10: rssiDiff -30 -> exponent 3.0 (inside both the built-in and example-config limits)
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("far", 10.0), -89));

        var results = new AbsorptionAvgOptimizer(_state).Optimize(snapshot, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes.ContainsKey("rx"), Is.True);
        Assert.That(results.Nodes["rx"].Absorption, Is.EqualTo(3.0).Within(1e-9));
    }

    [Test]
    public void Optimize_ProposesNothingWhenEveryMeasurementIsDegenerate()
    {
        var rx = NodeAt("rx", 0);
        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("at-one", 1.0), -100));
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("below-one", 0.5), -80));
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("co-located", 0.0), -40));

        var results = new AbsorptionAvgOptimizer(_state).Optimize(snapshot, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Empty);
    }

    [Test]
    public void Optimize_RejectsAverageOutsideAbsorptionLimits()
    {
        var rx = NodeAt("rx", 0);
        var snapshot = new OptimizationSnapshot();
        // rssiDiff -60 at d == 10 -> exponent 6.0, above the default max (4) and the example-config max (3.5)
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("far", 10.0), -119));

        var results = new AbsorptionAvgOptimizer(_state).Optimize(snapshot, new Dictionary<string, NodeSettings>());

        Assert.That(results.Nodes, Is.Empty);
    }
}
