using System.Text.Json;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Controllers;

/// <summary>
/// End-to-end redaction check for GET /api/state/config: builds a StateController over a
/// real ConfigLoader whose config.yaml contains a broker password, then serializes the
/// actual action result the way ASP.NET Core MVC does.
/// </summary>
public class StateControllerConfigRedactionTests
{
    private string _configDir = null!;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "ctrl-cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "config.yaml"),
            "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: http-secret\n");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task GetConfig_ResponseBody_OmitsMqttPassword()
    {
        var configLoader = new ConfigLoader(_configDir);
        await configLoader.ConfigAsync();

        var mqtt = new Mock<IMqttCoordinator>();
        var nodeTelemetryStore = new NodeTelemetryStore(mqtt.Object);
        var state = new State(configLoader, nodeTelemetryStore);
        var nodeSettingsStore = new Mock<NodeSettingsStore>(mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        var deviceSettingsStore = new Mock<DeviceSettingsStore>(mqtt.Object, state);

        var controller = new StateController(
            Mock.Of<ILogger<StateController>>(),
            state,
            configLoader,
            nodeSettingsStore.Object,
            deviceSettingsStore.Object,
            nodeTelemetryStore,
            new NodeStateMapper(nodeTelemetryStore, Mock.Of<IFirmwareTypeStore>()),
            new GlobalEventDispatcher());

        // Call the real action method.
        var config = controller.GetConfig();

        // Serialize exactly as ASP.NET Core MVC does for JSON action results.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var body = JsonSerializer.Serialize(config, options);

        Assert.That(config.Mqtt.Password, Is.EqualTo("http-secret"),
            "precondition: the password is present in the in-memory config");
        Assert.That(body, Does.Not.Contain("http-secret"), "GET /api/state/config leaked the password");
        Assert.That(body, Does.Not.Contain("password").IgnoreCase, "response emitted a password key");
        Assert.That(body, Does.Contain("broker.local"));

        await configLoader.StopAsync(CancellationToken.None);
        configLoader.Dispose();
    }
}
