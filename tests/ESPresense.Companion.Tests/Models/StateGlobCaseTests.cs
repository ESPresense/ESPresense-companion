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
            exclude_devices:
              - id: "iBeacon:BBBB*"
            """);

        _configLoader = new ConfigLoader(_configDir);
        _state = new State(_configLoader, new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object));

        for (var i = 0; i < 50 && _state.Config == null; i++) await Task.Delay(20);
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
    public void ShouldTrack_GlobsAreCaseInsensitive(string id, bool expected)
    {
        var device = new Device(id, null, TimeSpan.FromSeconds(30));
        Assert.That(_state.ShouldTrack(device), Is.EqualTo(expected));
    }
}
