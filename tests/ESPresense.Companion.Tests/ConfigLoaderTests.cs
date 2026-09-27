using System.Diagnostics;
using ESPresense.Services;

namespace ESPresense.Companion.Tests;

public class ConfigLoaderTests
{
    private string _tempDir = null!;
    private string _configPath = null!;

    private const string ValidYaml = "timeout: 45\nmqtt:\n  host: localhost\n";

    // Fails to deserialize into Config twice over: `timeout` is not an int, and the flow sequence is unterminated.
    private const string InvalidYaml = "timeout: not-a-number\ndevices:\n  - id: [unterminated\n";

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "espresense-configloader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yaml");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public void Constructor_DoesNoIoAndRaisesNoEvents()
    {
        var loader = new ConfigLoader(_tempDir);

        Assert.That(File.Exists(_configPath), Is.False, "constructor must not create config.yaml");
        Assert.That(loader.Config, Is.Null);
        Assert.That(loader.ConfigAsync().IsCompleted, Is.False);
    }

    [Test]
    public async Task LoadAsync_CreatesExampleConfigWhenMissing()
    {
        var loader = new ConfigLoader(_tempDir);
        var changes = 0;
        loader.ConfigChanged += (_, _) => changes++;

        await loader.LoadAsync();

        Assert.That(File.Exists(_configPath), Is.True);
        Assert.That(loader.Config, Is.Not.Null);
        Assert.That(changes, Is.EqualTo(1));

        // Second load of the freshly created file must be a no-op (mtime is read after creation).
        await loader.LoadAsync();
        Assert.That(changes, Is.EqualTo(1));
    }

    [Test]
    public async Task StartAsync_LoadsConfigBeforeReturning()
    {
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        var loader = new ConfigLoader(_tempDir);
        try
        {
            await loader.StartAsync(CancellationToken.None);

            Assert.That(loader.Config, Is.Not.Null);
            Assert.That(loader.Config!.Timeout, Is.EqualTo(45));
            Assert.That(loader.ConfigAsync().IsCompletedSuccessfully, Is.True);
        }
        finally
        {
            await loader.StopAsync(CancellationToken.None);
            loader.Dispose();
        }
    }

    [Test]
    public async Task ConfigAsync_InvalidYaml_WaitsWithoutSpinningAndHonoursCancellation()
    {
        await File.WriteAllTextAsync(_configPath, InvalidYaml);
        var loader = new ConfigLoader(_tempDir);

        await loader.LoadAsync();

        Assert.That(loader.Config, Is.Null);
        Assert.That(loader.ConfigAsync().IsCompleted, Is.False);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        Assert.CatchAsync<OperationCanceledException>(() => loader.ConfigAsync(cts.Token));

        wall.Stop();
        process.Refresh();
        var cpuUsed = process.TotalProcessorTime - cpuBefore;

        Assert.That(wall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "ConfigAsync must give up when the token is cancelled");
        // The old implementation busy-looped on a completed task, burning a full core for the whole wait.
        // A parked task should use a small fraction of the wall time even on a slow machine.
        Assert.That(cpuUsed, Is.LessThan(TimeSpan.FromMilliseconds(250)), $"ConfigAsync spun the CPU while waiting (used {cpuUsed.TotalMilliseconds:F0} ms over {wall.ElapsedMilliseconds} ms)");
        Assert.That(loader.Config, Is.Null);
    }

    [Test]
    public async Task ConfigChanged_ThrowingSubscriberDoesNotBlockOthersOrCauseReload()
    {
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        var loader = new ConfigLoader(_tempDir);
        var throwingCalls = 0;
        var goodCalls = 0;
        loader.ConfigChanged += (_, _) => { throwingCalls++; throw new InvalidOperationException("boom"); };
        loader.ConfigChanged += (_, _) => goodCalls++;

        await loader.LoadAsync();

        Assert.That(loader.Config, Is.Not.Null, "a throwing subscriber must not discard the parsed config");
        Assert.That(throwingCalls, Is.EqualTo(1));
        Assert.That(goodCalls, Is.EqualTo(1), "subscribers after the throwing one must still run");
        Assert.That(await loader.ConfigAsync(), Is.SameAs(loader.Config));

        // File unchanged: no re-parse, no re-invocation of any handler.
        await loader.LoadAsync();

        Assert.That(throwingCalls, Is.EqualTo(1));
        Assert.That(goodCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task LoadAsync_PicksUpValidFileAfterInvalidOneWhenMtimeChanges()
    {
        await File.WriteAllTextAsync(_configPath, InvalidYaml);
        var loader = new ConfigLoader(_tempDir);
        var changes = 0;
        loader.ConfigChanged += (_, _) => changes++;

        await loader.LoadAsync();
        Assert.That(loader.Config, Is.Null);
        Assert.That(changes, Is.EqualTo(0));

        // Same broken file again: still no config, still no event.
        await loader.LoadAsync();
        Assert.That(loader.Config, Is.Null);
        Assert.That(changes, Is.EqualTo(0));

        await File.WriteAllTextAsync(_configPath, ValidYaml);
        // Force a distinct mtime so the test does not depend on filesystem timestamp granularity.
        File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(2));

        await loader.LoadAsync();

        Assert.That(loader.Config, Is.Not.Null);
        Assert.That(loader.Config!.Timeout, Is.EqualTo(45));
        Assert.That(changes, Is.EqualTo(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.That(await loader.ConfigAsync(cts.Token), Is.SameAs(loader.Config));
    }

    [Test]
    public async Task LoadAsync_ReloadsWhenFileChanges()
    {
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        var loader = new ConfigLoader(_tempDir);
        var changes = 0;
        loader.ConfigChanged += (_, _) => changes++;

        await loader.LoadAsync();
        var first = loader.Config;
        Assert.That(first, Is.Not.Null);
        Assert.That(changes, Is.EqualTo(1));

        await File.WriteAllTextAsync(_configPath, "timeout: 99\n");
        File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(2));

        await loader.LoadAsync();

        Assert.That(changes, Is.EqualTo(2));
        Assert.That(loader.Config, Is.Not.SameAs(first));
        Assert.That(loader.Config!.Timeout, Is.EqualTo(99));
        // ConfigAsync resolves to the first successful load; later loads are observed via Config/ConfigChanged.
        Assert.That(await loader.ConfigAsync(), Is.SameAs(first));
    }
}
