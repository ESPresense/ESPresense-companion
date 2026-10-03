using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class StateGlobCaseTests
{
    private string _configDir = null!;
    private ConfigLoader _configLoader = null!;
    private State _state = null!;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        await File.WriteAllTextAsync(Path.Combine(_configDir, "config.yaml"), """
            devices:
              - id: "iBeacon:*"
              - name: "Phone *"
            exclude_devices:
              - id: "iBeacon:BBBB*"
              - name: "Phone GUEST*"
            """);

        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));

        // State subscribed to ConfigChanged above; the explicit load fires it synchronously.
        await _configLoader.LoadAsync();
        Assert.That(_state.Config, Is.Not.Null, "config never loaded");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
    }

    [TestCase("iBeacon:AAAA-1", true)]
    [TestCase("ibeacon:aaaa-1", true)]   // include glob must match regardless of case
    [TestCase("iBeacon:bbbb-1", false)]  // exclude glob must match regardless of case
    [TestCase("IBEACON:BBBB-1", false)]
    [TestCase("other:1", false)]
    public void ShouldTrack_IdGlobsAreCaseInsensitive(string id, bool expected)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30));
        Assert.That(_state.ShouldTrack(device), Is.EqualTo(expected));
    }

    [TestCase("Phone Bob", true)]
    [TestCase("phone bob", true)]        // include glob must match regardless of case
    [TestCase("Phone guest 1", false)]   // exclude glob must match regardless of case
    [TestCase("PHONE GUEST 1", false)]
    [TestCase("Tablet Bob", false)]
    public void ShouldTrack_NameGlobsAreCaseInsensitive(string name, bool expected)
    {
        var device = new Device("xyz:1", null, TimeSpan.FromSeconds(30)) { Name = name };
        Assert.That(_state.ShouldTrack(device), Is.EqualTo(expected));
    }

    // Autodiscovery has only the id and name off the wire, before any Device exists. A rotating-MAC
    // advertiser arrives under either shape depending on how the node fingerprinted it -- "name:x" as
    // the id, or a bare MAC id carrying the name -- so both must be excludable.
    [TestCase("iBeacon:BBBB-1", null, true)]
    [TestCase("IBEACON:BBBB-1", null, true)]        // case-insensitive, like the Device overload
    [TestCase("c0ffee001122", "Phone GUEST 7", true)]  // id says nothing; the name is what matches
    [TestCase("iBeacon:AAAA-1", null, false)]
    [TestCase("c0ffee001122", "Phone Bob", false)]
    [TestCase(null, null, false)]
    [TestCase("", "", false)]
    public void IsExcluded_MatchesOnIdOrNameWithoutADevice(string? id, string? name, bool expected)
    {
        Assert.That(_state.IsExcluded(id, name), Is.EqualTo(expected));
    }

    [Test]
    public void IsExcluded_AgreesWithShouldTrackForExcludedDevices()
    {
        // The pair overload is what discovery consults; it must not disagree with the Device path,
        // or an excluded device gets created and republished before the check untracks it.
        var device = new Device("iBeacon:BBBB-9", null, TimeSpan.FromSeconds(30));
        Assert.Multiple(() =>
        {
            Assert.That(_state.IsExcluded(device.Id, device.Name), Is.True);
            Assert.That(_state.ShouldTrack(device), Is.False);
        });
    }
}
