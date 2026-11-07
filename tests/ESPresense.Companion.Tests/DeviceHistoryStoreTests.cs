using ESPresense.Models;
using ESPresense.Services;
using SQLite;

namespace ESPresense.Companion.Tests;

public class DeviceHistoryStoreTests
{
    // Program.cs does this at startup; tests touching SQLite need it too.
    [OneTimeSetUp]
    public void InitSqlite() => SQLitePCL.Batteries.Init();

    private static async Task<ConfigLoader> LoadConfig(bool historyEnabled, string? path = null, string? workDir = null)
    {
        workDir ??= Path.Combine(Path.GetTempPath(), "espresense-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        await File.WriteAllTextAsync(Path.Combine(workDir, "config.yaml"), $@"
history:
  enabled: {historyEnabled.ToString().ToLowerInvariant()}
{(path == null ? "" : $"  path: {path}")}
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

    // sqlite-net hands back stored ticks as DateTimeKind.Unspecified; the JSON would then carry no Z and browsers would read it as local time.
    [Test]
    public async Task List_ReturnsWhenAsUtc()
    {
        // SQLiteAsyncConnection pools ":memory:" by connection string, so the tests here share one database: each uses its own id.
        var cfg = await LoadConfig(historyEnabled: true);
        var store = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), cfg);
        var when = new DateTime(2026, 11, 7, 10, 30, 0, DateTimeKind.Utc);

        await store.Add(new DeviceHistory { Id = "utc-dev", When = when, Best = true });

        var row = (await store.List("utc-dev"))![0];
        Assert.That(row.When!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
        Assert.That(row.When, Is.EqualTo(when));
        var ranged = (await store.List("utc-dev", when.AddMinutes(-1), when.AddMinutes(1)))![0];
        Assert.That(ranged.When!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task ListRange_BestOnly_LeavesOutTheOtherScenarios()
    {
        var cfg = await LoadConfig(historyEnabled: true);
        var store = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), cfg);
        var when = new DateTime(2026, 11, 7, 10, 30, 0, DateTimeKind.Utc);

        await store.Add(new DeviceHistory { Id = "best-dev", When = when, Scenario = "MultiFloor", Best = true, X = 1 });
        await store.Add(new DeviceHistory { Id = "best-dev", When = when, Scenario = "NearestNode", Best = false, X = 2 });
        await store.Add(new DeviceHistory { Id = "best-dev", When = when.AddSeconds(1), Scenario = "NearestNode", Best = true, X = 3 });

        var all = await store.List("best-dev", when.AddMinutes(-1), when.AddMinutes(1));
        var best = await store.List("best-dev", when.AddMinutes(-1), when.AddMinutes(1), bestOnly: true);

        Assert.That(all, Has.Count.EqualTo(3));
        Assert.That(best!.Select(r => r.X), Is.EqualTo(new double?[] { 1, 3 }));
    }

    [Test]
    public async Task HistoryPath_IsResolvedUnderTheStorageDir()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "espresense-history-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(workDir, ".storage");
        var cfg = await LoadConfig(historyEnabled: true, path: "sub/devices.db", workDir: workDir);
        var store = new DeviceHistoryStore(storageDir, cfg);

        await store.Add(new DeviceHistory { Id = "dev-1", When = DateTime.UtcNow, Best = true });

        Assert.That(File.Exists(Path.Combine(storageDir, "sub", "devices.db")), Is.True);
        Assert.That(await store.List("dev-1"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task HistoryPath_BlankFallsBackToDefault()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "espresense-history-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(workDir, ".storage");
        var cfg = await LoadConfig(historyEnabled: true, path: "", workDir: workDir);
        var store = new DeviceHistoryStore(storageDir, cfg);

        await store.Add(new DeviceHistory { Id = "dev-1", When = DateTime.UtcNow, Best = true });

        Assert.That(File.Exists(Path.Combine(storageDir, "history.db")), Is.True);
        Assert.That(await store.List("dev-1"), Has.Count.EqualTo(1));
    }
}
