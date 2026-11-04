using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging;
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

[TestFixture]
[NonParallelizable] // DeferredLoggerFactory is process-wide
public class DeferredLoggerFactoryTests
{
    [TearDown]
    public void TearDown() => DeferredLoggerFactory.Use(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private sealed class FailingLocator(Device device, Floor floor, State state, ILogger? logger = null)
        : ESPresense.Locators.BaseMultilateralizer(device, floor, state, logger)
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
    public void Locator_WithoutALogger_LogsThroughTheHostFactoryUnderItsOwnType()
    {
        var device = new Device("test-device-1", null, TimeSpan.FromSeconds(30));
        var locator = new FailingLocator(device, new Floor(), null!); // as State.cs builds them today (#1844)

        // The host's factory is set after the locator exists, as at startup.
        var provider = new FakeLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider));
        DeferredLoggerFactory.Use(factory);
        locator.Locate(new Scenario(null, locator, "test"));

        var record = provider.Collector.LatestRecord;
        Assert.That(record.Category, Is.EqualTo("ESPresense.Companion.Tests.Conventions.DeferredLoggerFactoryTests.FailingLocator"));
        Assert.That(record.Message, Does.EndWith(": boom"));
    }
}
