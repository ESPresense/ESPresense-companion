using ESPresense.Models;

namespace ESPresense.Companion.Tests.Models;

public class OptimizationDataSplitTests
{
    [Test]
    public void TryCreate_UsesOlderSnapshotsForTrainingAndNewestForValidation()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var snapshots = Enumerable.Range(0, 5)
            .Select(index => Snapshot(start.AddMinutes(index), index))
            .ToArray();

        var created = OptimizationDataSplit.TryCreate(snapshots, 0.4, out var split);

        Assert.That(created, Is.True);
        Assert.That(split, Is.Not.Null);
        Assert.That(split!.Training.Measures.Select(measure => measure.Rssi), Is.EqualTo(new[] { 0d, 1d, 2d }));
        Assert.That(split.Validation.SelectMany(snapshot => snapshot.Measures).Select(measure => measure.Rssi),
            Is.EqualTo(new[] { 3d, 4d }));
    }

    [Test]
    public void TryCreate_RequiresSeparateTrainingAndValidationSnapshots()
    {
        var snapshots = new[]
        {
            Snapshot(DateTime.UtcNow, 1),
            Snapshot(DateTime.UtcNow.AddSeconds(1), 2)
        };

        Assert.That(OptimizationDataSplit.TryCreate(snapshots, 0.2, out _), Is.False);
    }

    [Test]
    public void CombinedTrainingSnapshot_GroupsNodesByIdAcrossSamples()
    {
        var first = Snapshot(DateTime.UtcNow, 1);
        var second = Snapshot(DateTime.UtcNow.AddSeconds(1), 2);
        first.Measures[0].Rx = new OptNode { Id = "receiver" };
        second.Measures[0].Rx = new OptNode { Id = "RECEIVER" };
        var snapshots = new[] { first, second, Snapshot(DateTime.UtcNow.AddSeconds(2), 3) };

        OptimizationDataSplit.TryCreate(snapshots, 0.2, out var split);

        Assert.That(split!.Training.ByRx().Count, Is.EqualTo(1));
        Assert.That(split.Training.ByRx().Single().Count(), Is.EqualTo(2));
    }

    private static OptimizationSnapshot Snapshot(DateTime timestamp, double rssi)
    {
        return new OptimizationSnapshot
        {
            Timestamp = timestamp,
            Measures = new List<Measure> { new() { Rssi = rssi } }
        };
    }
}
