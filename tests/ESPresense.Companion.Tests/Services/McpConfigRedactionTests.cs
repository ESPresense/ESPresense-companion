using ESPresense.Models;
using ESPresense.Services;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Drives the real MCP <c>get_config</c> path end to end: a ConfigLoader backed by a
/// temp config.yaml that contains a broker password. The returned JSON must not contain it.
/// </summary>
public class McpConfigRedactionTests
{
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "mcp-cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "config.yaml"),
            "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: mcp-secret\n");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task GetConfigTool_DoesNotReturnMqttPassword()
    {
        var configLoader = new ConfigLoader(_configDir);
        await configLoader.ConfigAsync(); // ensure the YAML has been loaded

        var mqtt = new Mock<IMqttCoordinator>().Object;
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt);
        var state = new State(configLoader, nodeTelemetryStore);
        var nodeSettingsStore = new NodeSettingsStore(mqtt, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var firmwareUpdateJobs = new FirmwareUpdateJobService(
            nodeSettingsStore,
            nodeTelemetryStore,
            new HttpClient(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<FirmwareUpdateJobService>>());

        var sut = new McpResources(
            state,
            configLoader,
            nodeSettingsStore,
            nodeTelemetryStore,
            new DeviceSettingsStore(mqtt, state),
            new TelemetryService(CreateCoordinator()),
            firmwareUpdateJobs,
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()));

        var result = await sut.GetConfigTool();

        Assert.That(configLoader.Config!.Mqtt.Password, Is.EqualTo("mcp-secret"),
            "precondition: the password really is loaded from YAML");
        Assert.That(result, Does.Not.Contain("mcp-secret"), "MCP get_config leaked the broker password");
        Assert.That(result, Does.Not.Contain("password").IgnoreCase, "MCP get_config emitted a password key");
        Assert.That(result, Does.Contain("broker.local"));

        await configLoader.StopAsync(CancellationToken.None);
        configLoader.Dispose();
    }

    [Test]
    public async Task GetConfigResource_DoesNotReturnMqttPassword()
    {
        // state://config is a separate MCP entry point from the get_config tool and
        // must be redacted too.
        var configLoader = new ConfigLoader(_configDir);
        await configLoader.ConfigAsync();

        var mqtt = new Mock<IMqttCoordinator>().Object;
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt);
        var state = new State(configLoader, nodeTelemetryStore);
        var nodeSettingsStore = new NodeSettingsStore(mqtt, Mock.Of<Microsoft.Extensions.Logging.ILogger<NodeSettingsStore>>());
        var firmwareUpdateJobs = new FirmwareUpdateJobService(
            nodeSettingsStore,
            nodeTelemetryStore,
            new HttpClient(),
            Mock.Of<Microsoft.Extensions.Logging.ILogger<FirmwareUpdateJobService>>());

        var sut = new McpResources(
            state,
            configLoader,
            nodeSettingsStore,
            nodeTelemetryStore,
            new DeviceSettingsStore(mqtt, state),
            new TelemetryService(CreateCoordinator()),
            firmwareUpdateJobs,
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()));

        var result = await sut.GetConfigResource();

        Assert.That(configLoader.Config!.Mqtt.Password, Is.EqualTo("mcp-secret"),
            "precondition: the password really is loaded from YAML");
        Assert.That(result, Does.Not.Contain("mcp-secret"), "MCP state://config resource leaked the broker password");
        Assert.That(result, Does.Not.Contain("password").IgnoreCase, "MCP state://config resource emitted a password key");
        Assert.That(result, Does.Contain("broker.local"));

        await configLoader.StopAsync(CancellationToken.None);
        configLoader.Dispose();
    }

    private static MqttCoordinator CreateCoordinator()
    {
        var configLoader = new Mock<ConfigLoader>("test-config-dir");
        return new MqttCoordinator(
            configLoader.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<MqttCoordinator>>(),
            Mock.Of<MQTTnet.Diagnostics.Logger.IMqttNetLogger>(),
            new SupervisorConfigLoader(Mock.Of<Microsoft.Extensions.Logging.ILogger<SupervisorConfigLoader>>()));
    }
}
