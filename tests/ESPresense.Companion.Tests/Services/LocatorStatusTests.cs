using ESPresense.Companion.Tests.Models;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ESPresense.Utils;
using SQLite;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Pins LocatorState.Status, the string MultiScenarioLocator publishes over REST (/api/state/locator) and
/// the locatorStateChanged WebSocket event once it holds the locating lease.
/// </summary>
public class LocatorStatusTests
{
    private static async Task<string?> StatusFor(string locatorsYaml)
    {
        await using var fx = await YamlStateFixture.LoadAsync(locatorsYaml);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);
        var mqtt = new Mock<MqttCoordinator>(fx.Loader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), supervisor);
        var tracker = new DeviceTracker(fx.State, mqtt.Object, new TelemetryService(mqtt.Object), new GlobalEventDispatcher(),
            new DeviceSettingsStore(mqtt.Object, fx.State));
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), fx.Loader);

        var lease = new Mock<ILeaseService>();
        lease.Setup(l => l.AcquireAsync("locating", It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new LeaseHandle(lease.Object, "locating", NullLogger.Instance));
        lease.Setup(l => l.HasLease("locating")).Returns(true);

        var locator = new MultiScenarioLocator(tracker, fx.State, new DeviceStatePublisher(mqtt.Object), new DeviceHistoryWriter(history, fx.State), new GlobalEventDispatcher(), lease.Object, NullLogger<MultiScenarioLocator>.Instance);
        await locator.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (fx.State.LocatorState.Status is null or "Starting" or "Started" or "Acquiring lease..." && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            return fx.State.LocatorState.Status;
        }
        finally
        {
            await locator.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task NothingEnabled_ReportsNone()
    {
        Assert.That(await StatusFor("timeout: 30\n"), Is.EqualTo("None"));
    }

    [Test]
    public async Task NadarayaWatsonAndNearestNode_FollowScenarioOrder()
    {
        var status = await StatusFor("""
            locators:
              nadaraya_watson: { enabled: true }
              nearest_node: { enabled: true }
            """);
        Assert.That(status, Is.EqualTo("NadarayaWatson, NearestNode"));
    }

    [Test]
    public async Task SingleLocator_ReportsItsName()
    {
        Assert.That(await StatusFor("locators:\n  multi_floor:\n    enabled: true\n"), Is.EqualTo("MultiFloor"));
    }

    [Test]
    public async Task AllEnabled_ListsEveryLocator()
    {
        var status = await StatusFor("""
            locators:
              nadaraya_watson: { enabled: true }
              nelder_mead: { enabled: true }
              bfgs: { enabled: true }
              mle: { enabled: true }
              multi_floor: { enabled: true }
              nearest_node: { enabled: true }
            """);
        // #1844: the status is built from the same locator list as the scenarios, so it follows scenario order.
        // Before, a separate hand-written list put NadarayaWatson first.
        Assert.That(status, Is.EqualTo("NelderMead, Bfgs, Mle, MultiFloor, NadarayaWatson, NearestNode"));
    }
}
