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

        var (correlation, rmse, samples) = new OptimizationResults().Evaluate(new List<OptimizationSnapshot> { snapshot }, nss);

        Assert.That(double.IsFinite(correlation), Is.True);
        Assert.That(double.IsFinite(rmse), Is.True);
        Assert.That(samples, Is.EqualTo(2), "the co-located measure must not be counted");
    }
}
