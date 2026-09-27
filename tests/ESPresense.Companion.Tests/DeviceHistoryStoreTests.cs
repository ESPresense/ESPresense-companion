using ESPresense.Models;
using ESPresense.Services;
using SQLite;

namespace ESPresense.Companion.Tests;

public class DeviceHistoryStoreTests
{
    // Program.cs does this at startup; tests touching SQLite need it too.
    [OneTimeSetUp]
    public void InitSqlite() => SQLitePCL.Batteries.Init();

    private static async Task<ConfigLoader> LoadConfig(bool historyEnabled)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "espresense-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(Path.Combine(workDir, "config.yaml"), $@"
history:
  enabled: {historyEnabled.ToString().ToLowerInvariant()}
");
        var cfg = new ConfigLoader(workDir);
        await cfg.StartAsync(CancellationToken.None);
        await cfg.ConfigAsync();
        return cfg;
    }

    // Regression: the store used to flip a bool when CreateTableAsync finished, so a request
    // arriving first got an empty list instead of waiting for initialization.
    [Test]
    public async Task List_WaitsForInitialization_InsteadOfReturningNull()
    {
        var cfg = await LoadConfig(historyEnabled: true);
        var store = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), cfg);

        var history = await store.List("some-device");

        Assert.That(history, Is.Not.Null, "history should be readable as soon as the store is constructed");
        Assert.That(history, Is.Empty);
    }

    [Test]
    public async Task List_ReturnsNull_WhenHistoryDisabled()
    {
        var cfg = await LoadConfig(historyEnabled: false);
        var store = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), cfg);

        Assert.That(await store.List("some-device"), Is.Null);
        Assert.That(await store.Add(new DeviceHistory { Id = "some-device", When = DateTime.UtcNow }), Is.EqualTo(-1));
    }

    [Test]
    public async Task Add_ThenList_RoundTrips()
    {
        var cfg = await LoadConfig(historyEnabled: true);
        var store = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), cfg);

        await store.Add(new DeviceHistory { Id = "dev-1", When = DateTime.UtcNow, X = 1, Y = 2, Z = 3, Best = true });

        var history = await store.List("dev-1");
        Assert.That(history, Is.Not.Null.And.Count.EqualTo(1));
        Assert.That(history![0].X, Is.EqualTo(1));
    }
}
