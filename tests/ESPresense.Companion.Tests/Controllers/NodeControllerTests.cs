using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Controllers;

public class NodeControllerTests
{
    [Test]
    public async Task Update_InvalidUrl_ReturnsBadRequest()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var nodeSettingsStore = new NodeSettingsStore(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        DeviceSettingsStore? deviceSettingsStore = null;
        var lazyDss = new Lazy<DeviceSettingsStore>(() => deviceSettingsStore!);
        var state = new State(new Mock<ConfigLoader>("test-config-dir").Object, nodeTelemetryStore, nodeSettingsStore, lazyDss);
        deviceSettingsStore = new DeviceSettingsStore(mqtt.Object, state);

        var sut = new NodeController(nodeSettingsStore, nodeTelemetryStore, state);

        await sut.Update("node-1", new NodeController.NodeUpdate { Url = "https://example.com/firmware.bin" });

        mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task Update_MixedCaseTrustedUrl_ReturnsNoContent()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var nodeSettingsStore = new NodeSettingsStore(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        DeviceSettingsStore? deviceSettingsStore = null;
        var lazyDss = new Lazy<DeviceSettingsStore>(() => deviceSettingsStore!);
        var state = new State(new Mock<ConfigLoader>("test-config-dir").Object, nodeTelemetryStore, nodeSettingsStore, lazyDss);
        deviceSettingsStore = new DeviceSettingsStore(mqtt.Object, state);

        var sut = new NodeController(nodeSettingsStore, nodeTelemetryStore, state);

        await sut.Update("node-1", new NodeController.NodeUpdate
        {
            Url = "https://github.com/espresense/ESPresense/releases/download/v1/test.bin"
        });

        mqtt.Verify(x => x.EnqueueAsync("espresense/rooms/node-1/update/set", "https://github.com/espresense/ESPresense/releases/download/v1/test.bin", false), Times.Once);
    }
}
