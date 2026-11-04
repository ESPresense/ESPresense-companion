using ESPresense.Models;
using Microsoft.Extensions.Logging;

namespace ESPresense.Companion.Tests.Models;

/// <summary>
/// Pins which devices are tracked, which are excluded, and which configured name a tracked device ends up with.
/// Written against the pre-#1844 State.ShouldTrack so the extraction into DeviceTrackingRules can be checked
/// against it; only <see cref="Decide"/> knows where the rules live.
/// </summary>
public class TrackingRulesCharacterizationTests
{
    private YamlStateFixture _fx = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _fx = await YamlStateFixture.LoadAsync("""
            devices:
              - id: "literal:one"
                name: "Configured One"
              - id: "literal:two"
              - id: "glob:*"
                name: "Glob Name Is Not Applied"
              - name: "Exact Name"
              - name: "Wild *"
              - id: "  "
              - name: ""
              - id: "dup:1"
                name: "First"
              - id: "DUP:1"
                name: "Second"
            exclude_devices:
              - id: "glob:bad*"
              - id: "literal:two"
              - name: "Wild Guest*"
              - name: "Configured One"
            """);
    }

    [OneTimeTearDown]
    public async Task TearDown() => await _fx.DisposeAsync();

    /// <summary>Runs the tracking decision the way DeviceTracker.CheckDeviceAsync does and reports what the device looks like after.</summary>
    private (bool Track, string? Name) Decide(string id, string? name)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30)) { Name = name };
        var decision = _fx.State.TrackingRules.ShouldTrack(device.Id, device.Name);
        if (decision.ConfiguredName != null) device.Name = decision.ConfiguredName;
        Assert.That(_fx.State.ShouldTrack(new Device(id, null, TimeSpan.FromSeconds(30)) { Name = name }), Is.EqualTo(decision.Track),
            "State.ShouldTrack must agree with the rules");
        return (decision.Track, device.Name);
    }

    [TestCase("literal:one", null, true, "Configured One", TestName = "Literal id is tracked and takes its configured name")]
    [TestCase("LITERAL:ONE", "Advertised", true, "Configured One", TestName = "Literal id matches case-insensitively and overrides the advertised name")]
    [TestCase("literal:two", null, false, null, TestName = "Exclude beats a literal include")]
    [TestCase("glob:abc", "Advertised", true, "Advertised", TestName = "Id glob tracks but never applies the entry's name")]
    [TestCase("GLOB:ABC", null, true, null, TestName = "Id glob matches case-insensitively")]
    [TestCase("glob:bad1", null, false, null, TestName = "Exclude id glob beats include id glob")]
    [TestCase("GLOB:BAD1", null, false, null, TestName = "Exclude id glob is case-insensitive")]
    [TestCase("x:1", "Exact Name", true, "Exact Name", TestName = "Literal name is tracked")]
    [TestCase("x:1", "exact name", true, "exact name", TestName = "Literal name matches case-insensitively and keeps the device's own casing")]
    [TestCase("x:1", "Wild Thing", true, "Wild Thing", TestName = "Name glob is tracked")]
    [TestCase("x:1", "wild thing", true, "wild thing", TestName = "Name glob is case-insensitive")]
    [TestCase("x:1", "Wild Guest 3", false, "Wild Guest 3", TestName = "Exclude name glob beats include name glob")]
    [TestCase("x:1", "Configured One", false, "Configured One", TestName = "Exclude by name is checked against the advertised name")]
    [TestCase("x:1", null, false, null, TestName = "Unknown id without a name is not tracked")]
    [TestCase("x:1", "Someone Else", false, "Someone Else", TestName = "Unknown id and name are not tracked")]
    [TestCase("dup:1", null, true, "First", TestName = "Duplicate literal ids: the first entry wins")]
    public void ShouldTrack(string id, string? name, bool expectedTrack, string? expectedName)
    {
        var (track, after) = Decide(id, name);
        Assert.Multiple(() =>
        {
            Assert.That(track, Is.EqualTo(expectedTrack), "track");
            Assert.That(after, Is.EqualTo(expectedName), "name");
        });
    }

    [TestCase("glob:bad", null, true)]
    [TestCase("Glob:Bad-x", null, true)]
    [TestCase("literal:two", null, true)]
    [TestCase("LITERAL:TWO", null, true)]
    [TestCase("x:1", "Wild Guest", true)]
    [TestCase("x:1", "wild guest 9", true)]
    [TestCase("x:1", "configured one", true)]
    [TestCase("literal:one", null, false)]
    [TestCase("x:1", "Wild Thing", false)]
    [TestCase(null, null, false)]
    [TestCase("", "", false)]
    [TestCase("   ", "   ", false)]
    public void IsExcluded(string? id, string? name, bool expected)
    {
        Assert.That(_fx.State.IsExcluded(id, name), Is.EqualTo(expected));
    }

    [Test]
    public void ShouldTrack_HasNoSideEffects()
    {
        var device = new Device("literal:one", null, TimeSpan.FromSeconds(30)) { Name = "Advertised" };
        Assert.Multiple(() =>
        {
            Assert.That(_fx.State.ShouldTrack(device), Is.True);
            Assert.That(device.Name, Is.EqualTo("Advertised"), "the configured name is the caller's to apply");
            Assert.That(_fx.State.TrackingRules.ShouldTrack(device.Id, device.Name), Is.EqualTo(new TrackDecision(true, "Configured One")));
        });
    }

    [Test]
    public void Matching_NeverCompilesAGlob()
    {
        // Globs are compiled once per config load; IsExcluded used to Glob.Parse every exclude entry on every call.
        var rules = _fx.State.TrackingRules;
        var before = DeviceTrackingRules.GlobsCompiledOnThisThread;
        for (var i = 0; i < 1000; i++)
        {
            rules.IsExcluded($"glob:bad{i}", $"Wild Guest {i}");
            rules.IsExcluded($"x:{i}", "nobody");
            rules.ShouldTrack($"glob:{i}", $"Wild {i}");
            _fx.State.IsExcluded($"x:{i}", null);
            _fx.State.ShouldTrack(new Device($"x:{i}", null, TimeSpan.FromSeconds(30)));
        }
        Assert.That(DeviceTrackingRules.GlobsCompiledOnThisThread, Is.EqualTo(before));
    }

    [Test]
    public void FromConfig_CompilesEachPatternOnce()
    {
        var config = new Config
        {
            Devices = [new ConfigDevice { Id = "a:*", Name = "A *" }, new ConfigDevice { Id = "b" }],
            ExcludeDevices = [new ConfigDevice { Id = "a:x*" }, new ConfigDevice { Name = "  " }],
        };
        var before = DeviceTrackingRules.GlobsCompiledOnThisThread;
        DeviceTrackingRules.FromConfig(config);
        Assert.That(DeviceTrackingRules.GlobsCompiledOnThisThread - before, Is.EqualTo(4));
    }

    [TestCase("[a-")]
    [TestCase("[a-]")]
    public void FromConfig_SkipsOnlyTheInvalidPattern(string bad)
    {
        var config = new Config
        {
            Devices = [new ConfigDevice { Id = bad }, new ConfigDevice { Id = "good:*" }, new ConfigDevice { Name = bad }, new ConfigDevice { Name = "Good *" }],
            ExcludeDevices = [new ConfigDevice { Id = bad, Name = "Bad Name" }, new ConfigDevice { Id = "good:x*" }],
        };

        var rules = DeviceTrackingRules.FromConfig(config);

        Assert.Multiple(() =>
        {
            Assert.That(rules.ShouldTrack("good:1", null).Track, Is.True);
            Assert.That(rules.ShouldTrack("x:1", "Good One").Track, Is.True);
            Assert.That(rules.IsExcluded("good:x1", null), Is.True, "later exclude still applies");
            Assert.That(rules.IsExcluded("x:1", "Bad Name"), Is.True, "the valid half of an entry still applies");
            Assert.That(rules.ShouldTrack(bad, bad).Track, Is.False);
        });
    }

    [Test]
    public void FromConfig_LogsTheInvalidPattern()
    {
        var logger = new ListLogger();
        DeviceTrackingRules.FromConfig(new Config { ExcludeDevices = [new ConfigDevice { Id = "[a-" }] }, logger);
        Assert.That(logger.Entries, Has.One.Matches<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("[a-")));
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Test]
    public async Task InvalidPattern_DoesNotAbortConfigLoad()
    {
        await using var fx = await YamlStateFixture.LoadAsync("""
            filtering:
              motion_sigma: 7
            devices:
              - id: "[a-"
              - id: "good:*"
            exclude_devices:
              - name: "[a-]"
            """);
        var device = new Device("good:1", null, TimeSpan.FromSeconds(30));
        Assert.Multiple(() =>
        {
            Assert.That(fx.State.ShouldTrack(device), Is.True);
            Assert.That(fx.State.Config!.Filtering.MotionSigma, Is.EqualTo(7));
        });
    }

    [Test]
    public async Task NoConfiguredDevices_TracksNothingAndExcludesNothing()
    {
        await using var fx = await YamlStateFixture.LoadAsync("timeout: 30\n");
        var device = new Device("any:1", null, TimeSpan.FromSeconds(30)) { Name = "Any" };
        Assert.Multiple(() =>
        {
            Assert.That(fx.State.ShouldTrack(device), Is.False);
            Assert.That(fx.State.IsExcluded("any:1", "Any"), Is.False);
        });
    }
}
