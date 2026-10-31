using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests;

public class DeviceServiceTests
{
    private static (DeviceService service, State state, Mock<IMqttCoordinator> mqttMock) CreateService()
    {
        var mockConfigLoader = new Mock<ConfigLoader>("test-config-dir");
        var mqttMock = new Mock<IMqttCoordinator>();
        var nodeTelemetryStore = new NodeTelemetryStore(mqttMock.Object);
        var state = new State(mockConfigLoader.Object, nodeTelemetryStore);
        var events = new GlobalEventDispatcher();
        var logger = new Mock<ILogger<DeviceService>>();

        var service = new DeviceService(state, mqttMock.Object, events, logger.Object);
        return (service, state, mqttMock);
    }

    [Test]
    public async Task DeleteAsync_ClearsRetainedStateAndAttributesTopics()
    {
        var (service, state, mqttMock) = CreateService();
        var device = new Device("kitchen:phone", null, TimeSpan.FromSeconds(30)) { ReportedState = "kitchen" };
        state.Devices[device.Id] = device;

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", null, true))
            .Returns(Task.CompletedTask)
            .Verifiable();
        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}/attributes", null, true))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var deleted = await service.DeleteAsync(device.Id);

        Assert.That(deleted, Is.True);
        Assert.That(state.Devices.ContainsKey(device.Id), Is.False);
        mqttMock.Verify();
    }

    [Test]
    public async Task StampUnseenDevicesAsync_StampsAndPersistsOnlyDevicesWithoutLastSeen()
    {
        var (service, state, mqttMock) = CreateService();
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var seenAt = now.AddDays(-2);
        var restored = new Device("iBeacon:dead", "espresense_ibeacon_dead", TimeSpan.FromSeconds(30));
        var seen = new Device("phone:alive", null, TimeSpan.FromSeconds(30)) { LastSeen = seenAt };
        state.Devices[restored.Id] = restored;
        state.Devices[seen.Id] = seen;

        mqttMock.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);

        var stamped = await service.StampUnseenDevicesAsync(now);

        Assert.That(stamped, Is.EqualTo(1));
        Assert.That(restored.LastSeen, Is.EqualTo(now));
        Assert.That(seen.LastSeen, Is.EqualTo(seenAt));
        mqttMock.Verify(m => m.TryEnqueueAsync("espresense/companion/iBeacon:dead/attributes",
            It.Is<string>(p => p.Contains("\"last_seen\":\"2026-10-29T12:00:00Z\"")), true), Times.Once);
        mqttMock.Verify(m => m.TryEnqueueAsync("espresense/companion/phone:alive/attributes", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task StampedDevice_ExpiresOneRetentionPeriodLater()
    {
        var (service, state, mqttMock) = CreateService();
        mqttMock.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(true);
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var retention = TimeSpan.FromDays(30);
        var restored = new Device("tile:dead", "espresense_tile_dead", TimeSpan.FromSeconds(30));
        state.Devices[restored.Id] = restored;

        Assert.That(service.GetInactiveDevices(now - retention), Is.Empty, "unstamped: never matched");

        await service.StampUnseenDevicesAsync(now);

        Assert.That(service.GetInactiveDevices(now.AddDays(1) - retention), Is.Empty);
        Assert.That(service.GetInactiveDevices(now.AddDays(31) - retention), Has.Member(restored));
    }

    [Test]
    public async Task StampUnseenDevicesAsync_LeavesDeviceUnstampedWhenPublishFails()
    {
        var (service, state, mqttMock) = CreateService();
        mqttMock.Setup(m => m.TryEnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>())).ReturnsAsync(false);
        var restored = new Device("iBeacon:dead", "espresense_ibeacon_dead", TimeSpan.FromSeconds(30));
        state.Devices[restored.Id] = restored;

        var stamped = await service.StampUnseenDevicesAsync(DateTime.UtcNow);

        Assert.That(stamped, Is.EqualTo(0));
        Assert.That(restored.LastSeen, Is.Null, "left for the next pass to retry");
    }
}
