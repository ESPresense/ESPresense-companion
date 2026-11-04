using ESPresense.Models;
using ESPresense.Services;
using System.Text.Json;
using MathNet.Spatial.Euclidean;
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
            new FirmwareCatalogService(new HttpClient(), Mock.Of<IFirmwareTypeStore>(), nodeTelemetryStore),
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()));

        var result = await sut.RequestNodeUpdateTool("node-1", "https://example.com/firmware.bin");
        using var json = JsonDocument.Parse(result);

        Assert.That(json.RootElement.GetProperty("ok").GetBoolean(), Is.False);
        Assert.That(json.RootElement.GetProperty("error").GetString(), Is.EqualTo("Only ESPresense GitHub URLs are allowed"));
        mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task GetCalibrationTool_IncludesAnchoredDevices()
    {
        var configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(configDir);
        using var configLoader = new ConfigLoader(configDir);
        await configLoader.LoadAsync();

        var mqtt = new Mock<IMqttCoordinator>();
        var nodeSettingsStore = new NodeSettingsStore(mqtt.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        var state = new State(configLoader, nodeTelemetryStore);

        var rxNode = new Node("rx-node", NodeSourceType.Config);
        rxNode.Update(configLoader.Config!, new ConfigNode { Name = "RX Node", Point = new double[] { 5.0, 2.0, 0.5 } }, Enumerable.Empty<Floor>());
        state.Nodes["rx-node"] = rxNode;

        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30)) { Name = "Test Anchor" };
        device.SetAnchor(new DeviceAnchor(new Point3D(1.0, 2.0, 0.5), null, null));
        device.Nodes["rx-node"] = new DeviceToNode(device, rxNode) { Distance = 4.1, Rssi = -65, LastHit = DateTime.UtcNow };
        state.Devices["anchored-device"] = device;

        var sut = new McpResources(
            state,
            configLoader,
            nodeSettingsStore,
            nodeTelemetryStore,
            new DeviceSettingsStore(mqtt.Object, state),
            new TelemetryService(CreateMockMqttCoordinator()),
            new FirmwareUpdateJobService(nodeSettingsStore, nodeTelemetryStore, new HttpClient(), Mock.Of<Microsoft.Extensions.Logging.ILogger<FirmwareUpdateJobService>>()),
            new FirmwareCatalogService(new HttpClient(), Mock.Of<IFirmwareTypeStore>(), nodeTelemetryStore),
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()));

        using var json = JsonDocument.Parse(await sut.GetCalibrationTool());
        var root = json.RootElement;

        Assert.That(root.GetProperty("Anchored").EnumerateArray().Select(e => e.GetString()), Is.EquivalentTo(new[] { "Test Anchor" }));
        var cell = root.GetProperty("Matrix").GetProperty("Test Anchor").GetProperty("RX Node");
        Assert.That(cell.GetProperty("mapDistance").GetDouble(), Is.EqualTo(4.0).Within(0.01));
        Assert.That(cell.GetProperty("distance").GetDouble(), Is.EqualTo(4.1));
        Assert.That(cell.GetProperty("diff").GetDouble(), Is.EqualTo(0.1).Within(0.01));
        Assert.That(cell.GetProperty("percent").GetDouble(), Is.EqualTo(0.025).Within(0.001));

        await configLoader.StopAsync(CancellationToken.None);
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
