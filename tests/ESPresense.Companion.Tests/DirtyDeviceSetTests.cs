using ESPresense.Services;

namespace ESPresense.Companion.Tests;

public class DirtyDeviceSetTests
{
    private static async Task<List<string>> DrainAsync(DirtyDeviceSet set, int expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ids = new List<string>();
        await foreach (var id in set.ReadAllAsync(cts.Token))
        {
            ids.Add(id);
            if (ids.Count == expected) break;
        }
        return ids;
    }

    [Test]
    public async Task ReadAllAsync_LeftoversSurviveConsumerStoppingEarly()
    {
        var set = new DirtyDeviceSet();
        set.Mark("a");
        set.Mark("b");

        // The locator breaks out like this when it loses the lease.
        Assert.That(await DrainAsync(set, 1), Is.EqualTo(new[] { "a" }));

        Assert.That(set.Mark("b"), Is.False);
        Assert.That(await DrainAsync(set, 1), Is.EqualTo(new[] { "b" }));
    }

    [Test]
    public async Task ReadAllAsync_CancellationDoesNotStrandTheNextId()
    {
        var set = new DirtyDeviceSet();
        set.Mark("a");
        set.Mark("b");

        using var cts = new CancellationTokenSource();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in set.ReadAllAsync(cts.Token))
                cts.Cancel();
        });

        Assert.That(await DrainAsync(set, 1), Is.EqualTo(new[] { "b" }));
        Assert.That(set.Mark("b"), Is.True);
    }
}
