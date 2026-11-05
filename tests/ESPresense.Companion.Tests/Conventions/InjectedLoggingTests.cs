using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using MQTTnet.Diagnostics.Logger;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// The point of injecting <c>ILogger&lt;T&gt;</c> (#1846): what a service logs can be asserted, and it is
/// logged under the service's own category.
/// </summary>
[TestFixture]
public class InjectedLoggingTests
{
    [Test]
    public async Task NodeSettingsStore_LogsEachChangedSettingUnderItsOwnCategory()
    {
        var logger = new FakeLogger<NodeSettingsStore>();
        var store = new NodeSettingsStore(Mock.Of<IMqttCoordinator>(), logger);

        await store.Set("test-node-1", new NodeSettings("test-node-1") { Calibration = { Absorption = 2.5 } });

        var record = logger.Collector.GetSnapshot().Single();
        Assert.That(record.Level, Is.EqualTo(LogLevel.Information));
        Assert.That(record.Category, Is.EqualTo(typeof(NodeSettingsStore).FullName));
        Assert.That(record.Message, Is.EqualTo("Updating test-node-1 absorption: (empty) -> 2.50"));
        Assert.That(record.GetStructuredStateValue("NodeId"), Is.EqualTo("test-node-1"));
    }

    [Test]
    public async Task ConfigLoader_LogsAnUnreadableConfigWithTheException()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "logging-cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "   ");
        try
        {
            var logger = new FakeLogger<ConfigLoader>();
            using var loader = new ConfigLoader(dir, logger);

            await loader.LoadAsync();

            Assert.That(loader.Config, Is.Null);
            var error = logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Error);
            Assert.That(error.Exception, Is.TypeOf<InvalidDataException>());
            Assert.That(error.Message, Does.StartWith("Error reading"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void MqttNetLogger_ForwardsLevelAndSource()
    {
        var logger = new FakeLogger<MqttNetLogger>();
        var sut = new MqttNetLogger(logger);

        sut.Publish(MqttNetLogLevel.Warning, "MqttClient", "Disconnected from {0}", new object[] { "broker.test" }, null!);

        var record = logger.Collector.GetSnapshot().Single();
        Assert.That(record.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(record.Message, Is.EqualTo("Disconnected from broker.test"));
        var scope = record.Scopes.OfType<IEnumerable<KeyValuePair<string, object>>>().Single();
        Assert.That(scope, Does.Contain(new KeyValuePair<string, object>("Source", "MqttClient")));
    }
}

/// <summary>
/// Types built by State and DeviceTracker rather than by DI get their loggers from them, so they still log
/// through the host's factory under their own category.
/// </summary>
[TestFixture]
public class ConstructedLoggingTests
{
    private sealed class FailingLocator(Device device, Floor floor, State state, ILogger? logger = null)
        : BaseMultilateralizer(device, floor, state, logger)
    {
        public override bool Locate(Scenario scenario) =>
            HandleLocatorException(new InvalidOperationException("boom"), scenario, new MathNet.Spatial.Euclidean.Point3D()) > 0;
    }

    [Test]
    public void Locator_UsesTheInjectedLogger()
    {
        var logger = new FakeLogger();
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30));
        var locator = new FailingLocator(device, new Floor(), null!, logger);

        locator.Locate(new Scenario(null, locator, "test"));

        Assert.That(logger.Collector.LatestRecord.Level, Is.EqualTo(LogLevel.Error));
        Assert.That(logger.Collector.LatestRecord.Message, Does.StartWith("Error finding location for Id: test-device-1").And.EndWith(": boom"));
    }

    [Test]
    public void Locator_WithoutALogger_Discards()
    {
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30));
        var locator = new FailingLocator(device, new Floor(), null!);
        Assert.DoesNotThrow(() => locator.Locate(new Scenario(null, locator, "test")));
    }

    [Test]
    public void ScenarioFactory_GivesEachLocatorTypeALoggerFromTheStatesFactory()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        _ = new State(new ConfigLoader(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString())),
            new NodeTelemetryStore(Mock.Of<IMqttCoordinator>()), factory.Object);

        foreach (var type in new[]
                 {
                     typeof(ScenarioFactory), typeof(NelderMeadMultilateralizer), typeof(BfgsMultilateralizer),
                     typeof(MLEMultilateralizer), typeof(MultiFloorMultilateralizer),
                     typeof(NadarayaWatsonMultilateralizer),
                 })
            factory.Verify(f => f.CreateLogger(type.FullName!), Times.Once, type.Name);
    }

    [Test]
    public async Task DeviceTracker_LogsUnderItsOwnCategory_AndItsDevicesDiscoveryUnderAutoDiscovery()
    {
        await using var fx = await ESPresense.Companion.Tests.Models.YamlStateFixture.LoadAsync("""
            nodes:
              - id: node-a
                point: [1, 1, 1]
            """);
        var provider = new FakeLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));

        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(fx.Loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor) { CallBase = true };
        mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        var tracker = new DeviceTracker(fx.State, mqtt.Object, new TelemetryService(mqtt.Object), new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqtt.Object, fx.State), factory);

        // An unknown node is logged by the tracker itself.
        await tracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs { NodeId = "node-unknown", DeviceId = "dev:1", Payload = new DeviceMessage { Rssi = -60, Distance = 2 } });
        var unknown = provider.Collector.GetSnapshot().Single(r => r.Message == "Unknown node node-unknown");
        Assert.That(unknown.Category, Is.EqualTo(typeof(DeviceTracker).FullName));
        Assert.That(unknown.GetStructuredStateValue("NodeId"), Is.EqualTo("node-unknown"));

        // A device the tracker creates logs its discovery publish under AutoDiscovery.
        await tracker.OnDeviceMessageReceivedAsync(new DeviceMessageEventArgs { NodeId = "node-a", DeviceId = "dev:2", Payload = new DeviceMessage { Rssi = -60, Distance = 2 } });
        await fx.State.Devices["dev:2"].HassAutoDiscovery.Single().Send(mqtt.Object);
        var discovery = provider.Collector.GetSnapshot().Single(r => r.Message.StartsWith("[+] Discovery device_tracker"));
        Assert.That(discovery.Category, Is.EqualTo(typeof(AutoDiscovery).FullName));
    }

    [Test]
    public void DependencyInjection_PicksTheLoggerFactoryConstructors()
    {
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var mqtt = Mock.Of<IMqttCoordinator>();
        var configLoader = new ConfigLoader(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString()));

        var services = new ServiceCollection();
        services.AddSingleton(configLoader);
        services.AddSingleton(mqtt);
        services.AddSingleton(new NodeTelemetryStore(mqtt));
        services.AddSingleton(factory.Object);
        services.AddSingleton<State>();
        services.AddSingleton(new TelemetryService(new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(), new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance)).Object));
        services.AddSingleton<GlobalEventDispatcher>();
        services.AddSingleton(sp => new DeviceSettingsStore(mqtt, sp.GetRequiredService<State>()));
        services.AddSingleton<DeviceTracker>();

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<DeviceTracker>();

        factory.Verify(f => f.CreateLogger(typeof(ScenarioFactory).FullName!), Times.Once, "State");
        factory.Verify(f => f.CreateLogger(typeof(DeviceTracker).FullName!), Times.Once, "DeviceTracker");
        factory.Verify(f => f.CreateLogger(typeof(AutoDiscovery).FullName!), Times.Once, "DeviceTracker's discovery logger");
    }

    [Test]
    public async Task DependencyInjection_GivesMqttCoordinatorAnAutoDiscoveryLogger()
    {
        // Inbound discovery parse failures are logged by AutoDiscovery.TryDeserialize under its own category.
        var configLoader = new ConfigLoader(Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString()));
        var discoveryLogger = new FakeLogger<AutoDiscovery>();
        var services = new ServiceCollection();
        services.AddSingleton(configLoader);
        services.AddSingleton<ILogger<MqttCoordinator>>(NullLogger<MqttCoordinator>.Instance);
        services.AddSingleton<ILogger<AutoDiscovery>>(discoveryLogger);
        services.AddSingleton<IMqttNetLogger>(new MqttNetLogger());
        services.AddSingleton(new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        services.AddSingleton<MqttCoordinator>();

        using var sp = services.BuildServiceProvider();
        var mqtt = sp.GetRequiredService<MqttCoordinator>();

        await mqtt.OnMqttMessageReceived(ESPresense.Companion.Tests.Services.MqttRoutingCharacterizationTests.Message(
            "homeassistant/device_tracker/espresense-d/config", "{not json"));
        Assert.That(discoveryLogger.Collector.GetSnapshot().Select(r => r.Message), Has.Some.StartsWith("Failed to deserialize discovery message"));
    }
}
