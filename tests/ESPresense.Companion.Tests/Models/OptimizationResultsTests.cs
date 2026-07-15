using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class OptimizationResultsTests
{
    private NodeSettingsStore _nodeSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _nodeSettings = new NodeSettingsStore(
            Mock.Of<IMqttCoordinator>(),
            Mock.Of<ILogger<NodeSettingsStore>>());
    }

    [Test]
    public void EvaluateMetrics_UsesHuberLossAndCandidateValues()
    {
        var snapshots = new[]
        {
            Snapshot(distance: 10, measuredRssi: -87),
            Snapshot(distance: 10, measuredRssi: -87),
            Snapshot(distance: 10, measuredRssi: -107)
        };
        var baseline = new OptimizationResults().EvaluateMetrics(snapshots, _nodeSettings, huberDelta: 2);
        var candidate = new OptimizationResults
        {
            Nodes = new Dictionary<string, ProposedValues>
            {
                ["tx"] = new() { TxRefRssi = -60 }
            }
        }.EvaluateMetrics(snapshots, _nodeSettings, huberDelta: 2);

        Assert.That(candidate.HuberLoss, Is.LessThan(baseline.HuberLoss));
        Assert.That(candidate.SampleCount, Is.EqualTo(3));
        Assert.That(candidate.Rmse, Is.GreaterThan(10), "RMSE remains visible even when robust loss limits an outlier");
    }

    [Test]
    public void EvaluateMetrics_SkipsZeroDistanceMeasurements()
    {
        var snapshots = new[]
        {
            Snapshot(distance: 0, measuredRssi: -59),
            Snapshot(distance: 1, measuredRssi: -59)
        };

        var metrics = new OptimizationResults().EvaluateMetrics(snapshots, _nodeSettings);

        Assert.That(metrics.SampleCount, Is.EqualTo(1));
        Assert.That(metrics.HuberLoss, Is.Zero.Within(1e-9));
    }

    [Test]
    public void QuantizeForApplication_MatchesMqttPrecision()
    {
        var results = new OptimizationResults
        {
            Nodes = new Dictionary<string, ProposedValues>
            {
                ["node"] = new()
                {
                    Absorption = 2.746,
                    RxAdjRssi = 1.6,
                    TxRefRssi = -59.6
                }
            }
        };

        var quantized = results.QuantizeForApplication().Nodes["node"];

        Assert.That(quantized.Absorption, Is.EqualTo(2.75));
        Assert.That(quantized.RxAdjRssi, Is.EqualTo(2));
        Assert.That(quantized.TxRefRssi, Is.EqualTo(-60));
    }

    private static OptimizationSnapshot Snapshot(double distance, double measuredRssi)
    {
        var tx = new OptNode { Id = "tx", Location = new Point3D(0, 0, 0) };
        var rx = new OptNode { Id = "rx", Location = new Point3D(distance, 0, 0) };
        return new OptimizationSnapshot
        {
            Measures = new List<Measure>
            {
                new()
                {
                    Tx = tx,
                    Rx = rx,
                    Rssi = measuredRssi
                }
            }
        };
    }
}
