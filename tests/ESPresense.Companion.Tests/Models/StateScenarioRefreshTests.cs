using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ESPresense.Companion.Tests.Models;

/// <summary>Enabling or retuning a locator in config.yaml reaches devices that already exist, without a restart.</summary>
public class StateScenarioRefreshTests
{
    private const string Floors = """
        floors:
          - id: first
            name: First
            bounds: [[0, 0, 0], [10, 10, 3]]
        """;

    private static string Yaml(bool fingerprint, int timeout = 30) => $"""
        timeout: {timeout}
        {Floors}
        locators:
          nadaraya_watson:
            enabled: true
            floors: ["first"]
          fingerprint:
            enabled: {(fingerprint ? "true" : "false")}
            floors: ["first"]
        """;

    private string _dir = null!;
    private ConfigLoader _loader = null!;
    private State _state = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "scenario-refresh-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "config.yaml"), Yaml(fingerprint: false));
        _loader = new ConfigLoader(_dir);
        await _loader.StartAsync(CancellationToken.None);
        await _loader.ConfigAsync();
        var mqtt = new Mock<MqttCoordinator>(_loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(),
            new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        _state = new State(_loader, new NodeTelemetryStore(mqtt.Object));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _loader.StopAsync(CancellationToken.None);
        _loader.Dispose();
        Directory.Delete(_dir, true);
    }

    /// <summary>Rewrites config.yaml and waits for the loader to pick it up.</summary>
    private async Task Reload(string yaml)
    {
        var path = Path.Combine(_dir, "config.yaml");
        var seen = _loader.Config;
        await File.WriteAllTextAsync(path, yaml);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ReferenceEquals(_loader.Config, seen) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.That(_loader.Config, Is.Not.SameAs(seen), "config.yaml was not reloaded");
    }

    [Test]
    public async Task EnablingALocatorRebuildsExistingDevices()
    {
        var device = new Device("phone", null, TimeSpan.FromSeconds(30));
        _state.BuildScenarios(device);
        Assert.That(device.Scenarios, Has.Count.EqualTo(1));
        var before = device.Scenarios;

        await Reload(Yaml(fingerprint: true));

        Assert.That(_state.RefreshScenarios(device), Is.True);
        Assert.That(device.Scenarios, Has.Count.EqualTo(2), "the fingerprint locator joins without a restart");
        Assert.That(before, Has.Count.EqualTo(1), "the old list is replaced, not edited under a reader");
        Assert.That(_state.RefreshScenarios(device), Is.False, "rebuilt once per change");
    }

    [Test]
    public async Task OtherConfigChangesKeepScenarios()
    {
        var device = new Device("phone", null, TimeSpan.FromSeconds(30));
        _state.BuildScenarios(device);
        var scenarios = device.Scenarios;

        await Reload(Yaml(fingerprint: false, timeout: 45));

        Assert.That(_state.RefreshScenarios(device), Is.False);
        Assert.That(device.Scenarios, Is.SameAs(scenarios), "probabilities and filters survive unrelated edits");
    }

    [Test]
    public async Task HandBuiltScenariosAreLeftAlone()
    {
        var device = new Device("phone", null, TimeSpan.FromSeconds(30));
        await Reload(Yaml(fingerprint: true));
        Assert.That(_state.RefreshScenarios(device), Is.False);
        Assert.That(device.Scenarios, Is.Empty);
    }
}
