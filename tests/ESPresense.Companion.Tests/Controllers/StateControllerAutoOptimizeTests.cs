using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Controllers;

/// <summary>
/// POST /api/state/calibration/auto-optimize must change the config through the loader (save, reload, State.Apply),
/// never by mutating the Config instance State already holds.
/// </summary>
public class StateControllerAutoOptimizeTests
{
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ctrl-cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "config.yaml"),
            "timeout: 30\noptimization:\n  enabled: false\n  optimizer: per_node_absorption\n  training_window_mins: 20\n");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    private StateController CreateController(ConfigLoader configLoader, out State state)
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var nts = new NodeTelemetryStore(mqtt.Object);
        state = new State(configLoader, nts);
        var nss = new Mock<NodeSettingsStore>(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        var dss = new Mock<DeviceSettingsStore>(mqtt.Object, state);
        return new StateController(
            Mock.Of<ILogger<StateController>>(), state, configLoader, nss.Object, dss.Object, nts,
            new NodeStateMapper(nts, Mock.Of<IFirmwareTypeStore>()), new GlobalEventDispatcher(),
            new CalibrationService(state, nss.Object), new DeviceEnricher(dss.Object));
    }

    /// <summary>
    /// The toggle writes the whole optimization section back. A removed or unknown mode left in the file runs
    /// per_node_absorption with a warning at every load, so the save writes per_node_absorption instead of
    /// copying the stale value back.
    /// </summary>
    [TestCase("legacy")]
    [TestCase("global_absorption")]
    [TestCase("per-node-absorption")]
    public async Task Toggle_ReplacesARemovedOrUnknownModeWithTheOneThatRuns(string staleMode)
    {
        var path = Path.Combine(_configDir, "config.yaml");
        File.WriteAllText(path, $"timeout: 30\noptimization:\n  enabled: false\n  optimizer: {staleMode}\n  training_window_mins: 20\n");
        var configLoader = new ConfigLoader(_configDir);
        await configLoader.LoadAsync();
        var controller = CreateController(configLoader, out var state);
        Assume.That(state.Config!.Optimization.Optimizer, Is.EqualTo(staleMode));

        await controller.ToggleAutoOptimize(true);

        var yaml = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(yaml, Does.Contain("optimizer: per_node_absorption"));
            Assert.That(yaml, Does.Not.Contain(staleMode + "\n"), "the stale mode is gone from the file");
            Assert.That(state.Config!.Optimization.Optimizer, Is.EqualTo("per_node_absorption"));
            Assert.That(state.Config.Optimization.Enabled, Is.True);
            Assert.That(state.Config.Optimization.TrainingWindowMins, Is.EqualTo(20), "other optimization settings survive the save");
        });

        await configLoader.StopAsync(CancellationToken.None);
        configLoader.Dispose();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Toggle_SavesAndReloads_WithoutMutatingTheAppliedConfig(bool enable)
    {
        var configLoader = new ConfigLoader(_configDir);
        await configLoader.LoadAsync();
        var controller = CreateController(configLoader, out var state);

        var before = state.Config!;
        var result = await controller.ToggleAutoOptimize(enable);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(before.Optimization.Enabled, Is.False, "the previously applied Config was mutated in place");
            Assert.That(state.Config, Is.Not.SameAs(before), "the reload should hand State a new Config");
            Assert.That(state.Config!.Optimization.Enabled, Is.EqualTo(enable));
            Assert.That(state.Config.Optimization.Optimizer, Is.EqualTo("per_node_absorption"), "other optimization settings survive the save");
            Assert.That(state.Config.Optimization.TrainingWindowMins, Is.EqualTo(20));
        });

        await configLoader.StopAsync(CancellationToken.None);
        configLoader.Dispose();
    }
}
