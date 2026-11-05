using ESPresense.Companion.Tests.Models;
using ESPresense.Models;
using ESPresense.Optimizers;

namespace ESPresense.Companion.Tests.Optimizers;

public class OptimizationSnapshotStoreTests
{
    private static OptimizationSnapshot At(DateTime when, int measures = 1)
    {
        var s = new OptimizationSnapshot { Timestamp = when };
        for (var i = 0; i < measures; i++) s.Measures.Add(new Measure());
        return s;
    }

    [Test]
    public async Task Take_DropsSnapshotsOlderThanTheTrainingWindow()
    {
        await using var fx = await YamlStateFixture.LoadAsync("optimization:\n  training_window_mins: 10\n");
        var store = new OptimizationSnapshotStore();
        var now = DateTime.UtcNow;
        store.AddRange([At(now.AddMinutes(-11), 2), At(now.AddMinutes(-9), 3)]);

        var taken = store.Take(fx.State);

        Assert.Multiple(() =>
        {
            Assert.That(store.ToArray(), Has.Length.EqualTo(2));
            Assert.That(store.ToArray().Last(), Is.SameAs(taken));
            Assert.That(store.Count, Is.EqualTo(2));
            Assert.That(store.MeasurementCount, Is.EqualTo(3 + taken.Measures.Count));
        });
    }

    [Test]
    public async Task Take_DefaultsToAThirtyMinuteWindow()
    {
        await using var fx = await YamlStateFixture.LoadAsync("timeout: 30\n");
        var store = new OptimizationSnapshotStore();
        var now = DateTime.UtcNow;
        store.AddRange([At(now.AddMinutes(-31)), At(now.AddMinutes(-29))]);

        store.Take(fx.State);

        Assert.That(store.Count, Is.EqualTo(2));
    }
}
