using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class NodeBiasTests
{
    private static readonly OptNode[] Grid =
    [
        new() { Id = "a", Location = new Point3D(0, 0, 1) },
        new() { Id = "b", Location = new Point3D(8, 0, 1) },
        new() { Id = "c", Location = new Point3D(0, 8, 1) },
        new() { Id = "d", Location = new Point3D(8, 8, 1) },
        new() { Id = "e", Location = new Point3D(4, 12, 1) },
    ];

    private static OptimizationSnapshot Snapshot(Func<OptNode, OptNode, double> factor)
    {
        var snapshot = new OptimizationSnapshot();
        foreach (var tx in Grid)
            foreach (var rx in Grid.Where(r => r != tx))
                snapshot.Measures.Add(new Measure { Tx = tx, Rx = rx, Distance = tx.Location.DistanceTo(rx.Location) * factor(tx, rx) });
        return snapshot;
    }

    [Test]
    public void Find_NamesTheNodeReadingLongAgainstEveryNeighbour()
    {
        var bias = NodeBias.Find(Snapshot((tx, rx) => tx.Id == "e" || rx.Id == "e" ? 1.5 : 1)).Single();

        Assert.That(bias.Id, Is.EqualTo("e"));
        Assert.That(bias.Percent, Is.EqualTo(0.5).Within(0.01));
        Assert.That(bias.Links, Is.EqualTo(8));
        Assert.That(bias.Hint, Does.Contain("50% long"));
    }

    [Test]
    public void Find_IgnoresANetworkThatReadsLongEverywhere()
    {
        Assert.That(NodeBias.Find(Snapshot((_, _) => 1.5)), Is.Empty);
    }

    [Test]
    public void Find_IgnoresASingleBadLink()
    {
        Assert.That(NodeBias.Find(Snapshot((tx, rx) => tx.Id == "a" && rx.Id == "b" ? 3 : 1)), Is.Empty);
    }

    [Test]
    public void Find_HandlesNoMeasures()
    {
        Assert.That(NodeBias.Find(new OptimizationSnapshot()), Is.Empty);
    }
}
