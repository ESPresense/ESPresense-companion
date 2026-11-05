using ESPresense.Models;
using ESPresense.Optimizers;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace ESPresense.Companion.Tests.Optimizers;

/// <summary>
/// per_node_absorption is the only optimizer mode. The legacy chain and global_absorption were removed, so a
/// config that still names one, or names nothing, must run per_node_absorption rather than nothing.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OptimizerModeTests
{
    private string _configDir = null!;
    private string _configPath = null!;
    private ConfigLoader _configLoader = null!;
    private FakeLogger<OptimizationRunner> _log = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configPath = Path.Combine(_configDir, "config.yaml");
        _configLoader = new ConfigLoader(_configDir);
        _log = new FakeLogger<OptimizationRunner>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task WriteConfigAsync(string? optimizer, DateTime modified)
    {
        var line = optimizer == null ? "" : $"  optimizer: {optimizer}\n";
        await File.WriteAllTextAsync(_configPath, $"optimization:\n  enabled: false\n{line}");
        // The loader skips a file whose timestamp has not moved, so make each rewrite visibly newer.
        File.SetLastWriteTimeUtc(_configPath, modified);
        await _configLoader.LoadAsync();
    }

    private OptimizationRunner MakeRunner()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var state = new State(_configLoader, new NodeTelemetryStore(mqtt.Object));
        var nss = new NodeSettingsStore(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        return new OptimizationRunner(state, nss, _log, _configLoader, Mock.Of<ILeaseService>(), NullLoggerFactory.Instance);
    }

    private List<string> Warnings() =>
        _log.Collector.GetSnapshot().Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

    [TestCase(null)]
    [TestCase("per_node_absorption")]
    [TestCase("Per_Node_Absorption")]
    public async Task UnsetOrPerNode_BuildsPerNodeAbsorption_WithoutWarning(string? optimizer)
    {
        await WriteConfigAsync(optimizer, DateTime.UtcNow);

        var runner = MakeRunner();

        Assert.That(runner.CurrentOptimizers.Select(o => o.GetType()), Is.EqualTo(new[] { typeof(PerNodeAbsorptionRxTx) }));
        Assert.That(Warnings(), Is.Empty);
    }

    [TestCase("legacy")]
    [TestCase("global_absorption")]
    [TestCase("LEGACY")]
    public async Task RemovedMode_BuildsPerNodeAbsorption_AndWarnsOnceNamingIt(string optimizer)
    {
        await WriteConfigAsync(optimizer, DateTime.UtcNow);

        var runner = MakeRunner();

        Assert.That(runner.CurrentOptimizers.Select(o => o.GetType()), Is.EqualTo(new[] { typeof(PerNodeAbsorptionRxTx) }));
        var warnings = Warnings();
        Assert.That(warnings, Has.Count.EqualTo(1), string.Join("; ", warnings));
        Assert.That(warnings[0], Does.Contain(optimizer.ToLowerInvariant()).And.Contain("removed").And.Contain("per_node_absorption"));
    }

    [Test]
    public async Task SwitchingToRemovedModeAtRuntime_KeepsPerNodeAbsorption_AndWarnsOnce()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        await WriteConfigAsync("per_node_absorption", t0);
        var runner = MakeRunner();
        Assert.That(Warnings(), Is.Empty);

        await WriteConfigAsync("legacy", t0.AddMinutes(1));
        Assert.That(runner.CurrentOptimizers.Select(o => o.GetType()), Is.EqualTo(new[] { typeof(PerNodeAbsorptionRxTx) }));
        Assert.That(Warnings(), Has.Count.EqualTo(1));
        Assert.That(Warnings()[0], Does.Contain("legacy"));

        // A later reload that still says legacy does not repeat the warning.
        await WriteConfigAsync("legacy", t0.AddMinutes(2));
        Assert.That(Warnings(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UnknownMode_BuildsPerNodeAbsorption_AndWarns()
    {
        await WriteConfigAsync("bogus", DateTime.UtcNow);

        var runner = MakeRunner();

        Assert.That(runner.CurrentOptimizers.Select(o => o.GetType()), Is.EqualTo(new[] { typeof(PerNodeAbsorptionRxTx) }));
        Assert.That(Warnings(), Has.Count.EqualTo(1));
        Assert.That(Warnings()[0], Does.Contain("bogus"));
    }
}
