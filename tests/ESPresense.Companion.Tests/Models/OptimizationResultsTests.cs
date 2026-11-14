using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class OptimizationResultsTests
{
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
    public void Evaluate_SkipsMeasuresWithZeroMapDistance()
    {
        var nss = new NodeSettingsStore(new Mock<IMqttCoordinator>().Object, Mock.Of<ILogger<NodeSettingsStore>>());
        var rx = NodeAt("rx", 0);

        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("far", 10.0), -86));
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("near", 2.0), -67));
        // Co-located with the receiver: Log10(0) would be -Infinity and poison the whole evaluation
        snapshot.Measures.Add(MeasureOf(rx, NodeAt("co-located", 0.0), -40));

        var (correlation, rmse, samples, _) = new OptimizationResults().Evaluate(new List<OptimizationSnapshot> { snapshot }, nss);

        Assert.That(double.IsFinite(correlation), Is.True);
        Assert.That(double.IsFinite(rmse), Is.True);
        Assert.That(samples, Is.EqualTo(2), "the co-located measure must not be counted");
    }

    [Test]
    public void QuantizeForApplication_MatchesTheAppliedPrecision()
    {
        var results = new OptimizationResults
        {
            Nodes = new Dictionary<string, ProposedValues>
            {
                ["node"] = new() { Absorption = 2.746, RxAdjRssi = 1.6, TxRefRssi = -59.6 }
            }
        };

        var quantized = results.QuantizeForApplication().Nodes["node"];

        Assert.That(quantized.Absorption, Is.EqualTo(2.75));
        Assert.That(quantized.RxAdjRssi, Is.EqualTo(2));
        Assert.That(quantized.TxRefRssi, Is.EqualTo(-60));
    }

    [Test]
    public void Score_ReportsDistanceRmse_ThatMovesWithGaugeShift()
    {
        // Three links (two would make R trivially ±1), exact RSSI under txRef=-59, n=2.7. A k dB gauge shift keeps Pearson R at 1.0
        // but must move DistanceRmse away from 0 (issue #1868: R is blind to range bias).
        var rx = NodeAt("rx", 0);
        var txA = NodeAt("txA", 4.0);
        var txB = NodeAt("txB", 9.0);
        var txC = NodeAt("txC", 2.0);
        static double Rssi(double d) => -59 - 10 * 2.7 * Math.Log10(d);
        var snapshot = new OptimizationSnapshot();
        snapshot.Measures.Add(MeasureOf(rx, txA, Rssi(4.0)));
        snapshot.Measures.Add(MeasureOf(rx, txB, Rssi(9.0)));
        snapshot.Measures.Add(MeasureOf(rx, txC, Rssi(2.0)));

        var nss = new NodeSettingsStore(new Mock<IMqttCoordinator>().Object, Mock.Of<ILogger<NodeSettingsStore>>());
        var exact = new OptimizationResults().Evaluate(new List<OptimizationSnapshot> { snapshot }, nss);
        Assert.That(exact.DistanceRmse, Is.EqualTo(0.0).Within(1e-9), "exact calibration should imply exact distances");

        // Propose every txRef 6 dB hotter: predicted RSSI moves by the same 6 dB everywhere, so R stays 1.
        var shifted = new OptimizationResults
        {
            Nodes = new Dictionary<string, ProposedValues>
            {
                ["txA"] = new() { TxRefRssi = -53 },
                ["txB"] = new() { TxRefRssi = -53 },
                ["txC"] = new() { TxRefRssi = -53 },
            }
        };
        var eval = shifted.Evaluate(new List<OptimizationSnapshot> { snapshot }, nss);
        Assert.That(eval.DistanceRmse, Is.GreaterThan(0.5), "a 6 dB gauge shift must show up in distance space even though R stays 1");
        Assert.That(eval.Correlation, Is.EqualTo(1.0).Within(1e-9), "R is (wrongly) happy with the shifted calibration, which is the point");
    }
}
