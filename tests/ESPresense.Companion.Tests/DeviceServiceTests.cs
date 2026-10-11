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
}
