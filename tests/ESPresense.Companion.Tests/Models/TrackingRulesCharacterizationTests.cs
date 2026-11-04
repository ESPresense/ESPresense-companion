using ESPresense.Models;

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

    /// <summary>Runs the tracking decision the way DeviceTracker does and reports what the device looks like after.</summary>
    private (bool Track, string? Name) Decide(string id, string? name)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30)) { Name = name };
        var track = _fx.State.ShouldTrack(device);
        return (track, device.Name);
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
