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

        // The old implementation looped on an already-completed task without ever yielding, so it never
        // returned a task at all (the IsCompleted check above would have hung). Process-wide CPU time is
        // too noisy to assert on a shared CI runner, so pin the behaviour structurally instead: the call
        // returns a pending task, it stays pending while the file is invalid, and cancellation ends it.
        using var cts = new CancellationTokenSource();
        var pending = loader.ConfigAsync(cts.Token);
        await Task.Delay(200);
        Assert.That(pending.IsCompleted, Is.False, "ConfigAsync must stay pending while the config is invalid");

        var wall = Stopwatch.StartNew();
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        wall.Stop();

        Assert.That(wall.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), "ConfigAsync must give up when the token is cancelled");
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
        // ConfigAsync only gates on the first load; it must hand back the current config, not the first one
        // (MqttCoordinator calls it again when re-initialising after a config change).
        Assert.That(await loader.ConfigAsync(), Is.SameAs(loader.Config));
    }

    [Test]
    public async Task SaveSectionAsync_ReplacesAWholeCrlfSectionWithABlankLineInside()
    {
        await File.WriteAllTextAsync(_configPath, "optimization:\r\n  enabled: false\r\n\r\n  interval_secs: 99\r\ntimeout: 45\r\n");
        var loader = new ConfigLoader(_tempDir);
        await loader.LoadAsync();

        await loader.SaveSectionAsync("optimization", new Dictionary<string, object> { ["enabled"] = true });

        var text = await File.ReadAllTextAsync(_configPath);
        Assert.That(text, Does.Not.Contain("interval_secs"), "no line of the old section may survive");
        Assert.That(loader.Config!.Optimization.Enabled, Is.True);
        Assert.That(loader.Config.Timeout, Is.EqualTo(45));
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task SaveSectionAsync_KeepsTheFileMode()
    {
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        File.SetUnixFileMode(_configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var loader = new ConfigLoader(_tempDir);
        await loader.LoadAsync();

        await loader.SaveSectionAsync("timeout", 60);

        Assert.That(File.GetUnixFileMode(_configPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "config.yaml holds the MQTT password; a save must not widen its permissions");
    }

    [Test]
    [Platform(Exclude = "Win")]
    public async Task SaveSectionAsync_WritesThroughASymlink()
    {
        var realDir = Path.Combine(_tempDir, "real");
        Directory.CreateDirectory(realDir);
        var realPath = Path.Combine(realDir, "my-config.yaml");
        await File.WriteAllTextAsync(realPath, ValidYaml);
        File.CreateSymbolicLink(_configPath, realPath);
        var loader = new ConfigLoader(_tempDir);
        await loader.LoadAsync();

        await loader.SaveSectionAsync("timeout", 60);

        Assert.That(new FileInfo(_configPath).LinkTarget, Is.EqualTo(realPath), "the save must not replace the symlink");
        Assert.That(await File.ReadAllTextAsync(realPath), Does.Contain("timeout: 60"));
        Assert.That(loader.Config!.Timeout, Is.EqualTo(60));
        Assert.That(Directory.GetFiles(realDir, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task SaveSectionAsync_AppliesImmediatelyAndNeverYieldsBlankConfig()
    {
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        var loader = new ConfigLoader(_tempDir);
        await loader.LoadAsync();
        Assert.That(loader.Config!.Mqtt.Host, Is.EqualTo("localhost"));

        var blankConfigsSeen = 0;
        var changes = 0;
        loader.ConfigChanged += (_, c) =>
        {
            changes++;
            // A blank Config (from an empty or truncated file) has default Timeout and no MQTT host.
            if (c.Mqtt?.Host != "localhost") blankConfigsSeen++;
        };

        // Save applies the change itself (no wait for the poll) and the rest of the file survives.
        await loader.SaveSectionAsync("timeout", 60);
        Assert.That(loader.Config!.Timeout, Is.EqualTo(60));
        Assert.That(loader.Config.Mqtt.Host, Is.EqualTo("localhost"));
        Assert.That(changes, Is.EqualTo(1));

        // A load straight after the save is a no-op: the save already loaded the new content.
        await loader.LoadAsync();
        Assert.That(changes, Is.EqualTo(1));

        // Hammer save and load concurrently: the write is atomic (temp file + rename) and the read-modify-write
        // holds the load lock, so no observer ever sees a truncated file parsed as an empty config.
        for (var i = 0; i < 25; i++)
            await Task.WhenAll(loader.SaveSectionAsync("timeout", 100 + i), loader.LoadAsync(), loader.LoadAsync());

        Assert.That(blankConfigsSeen, Is.EqualTo(0), "a save/load race produced a blank config");
        Assert.That(loader.Config!.Timeout, Is.EqualTo(124));
        Assert.That(loader.Config.Mqtt.Host, Is.EqualTo("localhost"));
        Assert.That(Directory.GetFiles(_tempDir, "*.tmp"), Is.Empty, "temp files must not be left behind");
    }

    [Test]
    public async Task LoadAsync_EmptyFile_IsAFailureNotABlankConfig()
    {
        // Empty from the start: no config at all, ConfigAsync stays pending.
        await File.WriteAllTextAsync(_configPath, "   \n\n");
        var loader = new ConfigLoader(_tempDir);
        var changes = 0;
        loader.ConfigChanged += (_, _) => changes++;

        await loader.LoadAsync();
        Assert.That(loader.Config, Is.Null);
        Assert.That(changes, Is.EqualTo(0));
        Assert.That(loader.ConfigAsync().IsCompleted, Is.False);

        // Valid content arrives: loaded normally.
        await File.WriteAllTextAsync(_configPath, ValidYaml);
        File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(2));
        await loader.LoadAsync();
        var loaded = loader.Config;
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Timeout, Is.EqualTo(45));
        Assert.That(changes, Is.EqualTo(1));

        // File truncated to nothing (e.g. an editor that writes in place): previous config is kept, no event.
        await File.WriteAllTextAsync(_configPath, string.Empty);
        File.SetLastWriteTimeUtc(_configPath, DateTime.UtcNow.AddSeconds(4));
        await loader.LoadAsync();
        Assert.That(loader.Config, Is.SameAs(loaded));
        Assert.That(changes, Is.EqualTo(1));
    }
}
