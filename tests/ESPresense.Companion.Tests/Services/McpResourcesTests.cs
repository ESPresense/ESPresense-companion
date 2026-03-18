using AutoMapper;
using ESPresense.Models;
using ESPresense.Services;
using System.Text.Json;
using Moq;

namespace ESPresense.Companion.Tests.Services;

public class McpResourcesTests
{
    [Test]
    public async Task RequestNodeUpdateTool_InvalidUrl_ReturnsError()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var nodeSettingsStore = new NodeSettingsStore(mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        var state = new State(new Mock<ConfigLoader>("test-config-dir").Object, nodeTelemetryStore);
        var firmwareUpdateJobs = new FirmwareUpdateJobService(
            nodeSettingsStore,
            nodeTelemetryStore,
            new HttpClient(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<FirmwareUpdateJobService>>());
        var telemetryService = new TelemetryService(CreateMockMqttCoordinator());

        var sut = new McpResources(
            state,
            new Mock<ConfigLoader>("test-config-dir").Object,
            nodeSettingsStore,
            nodeTelemetryStore,
            new DeviceSettingsStore(mqtt.Object, state),
            telemetryService,
            firmwareUpdateJobs,
            Mock.Of<IMapper>());

        var result = await sut.RequestNodeUpdateTool("node-1", "https://example.com/firmware.bin");
        using var json = JsonDocument.Parse(result);

        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(json.RootElement.GetProperty("error").GetString(), Is.EqualTo("Only ESPresense GitHub URLs are allowed"));
        mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    private static MqttCoordinator CreateMockMqttCoordinator()
    {
        var mockConfigLoader = new Mock<ConfigLoader>("test-config-dir");
        var mockLogger = new Mock<Microsoft.Extensions.Logging.ILogger<MqttCoordinator>>();
        var mockMqttNetLogger = new Mock<MQTTnet.Diagnostics.Logger.IMqttNetLogger>();
        var mockSupervisorLogger = new Mock<Microsoft.Extensions.Logging.ILogger<SupervisorConfigLoader>>();
        var supervisorConfigLoader = new SupervisorConfigLoader(mockSupervisorLogger.Object);

        return new MqttCoordinator(
            mockConfigLoader.Object,
            mockLogger.Object,
            mockMqttNetLogger.Object,
            supervisorConfigLoader);
    }
}
