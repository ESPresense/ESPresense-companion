using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using ESPresense.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SQLite;
using YamlDotNet.Serialization;
using System.IO;
using System.Threading.Tasks;
using MathNet.Spatial.Euclidean;
using ESPresense.Locators;

namespace ESPresense.Companion.Tests;

public class FilteringTests
{
    [Test]
    public void TestFilteringDeserialization()
    {
        string yaml = @"
        filtering:
          process_noise: 0.05
          measurement_noise: 0.2
          max_velocity: 1.5
          smoothing_weight: 0.8
          motion_sigma: 3.0
        ";

        var deserializer = new DeserializerBuilder().Build();
        var config = deserializer.Deserialize<Config>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Filtering);

        Assert.That(config.Filtering.ProcessNoise, Is.EqualTo(0.05));
        Assert.That(config.Filtering.MeasurementNoise, Is.EqualTo(0.2));
        Assert.That(config.Filtering.MaxVelocity, Is.EqualTo(1.5));
        Assert.That(config.Filtering.SmoothingWeight, Is.EqualTo(0.8));
        Assert.That(config.Filtering.MotionSigma, Is.EqualTo(3.0));
    }

    [Test]
    public void TestDefaultFilteringValues()
    {
        string yaml = @"
        mqtt:
          host: localhost
        ";

        var deserializer = new DeserializerBuilder().Build();
        var config = deserializer.Deserialize<Config>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Filtering);

        // Verify defaults match the hardcoded values we replaced
        Assert.That(config.Filtering.ProcessNoise, Is.EqualTo(0.01));
        Assert.That(config.Filtering.MeasurementNoise, Is.EqualTo(0.1));
        Assert.That(config.Filtering.MaxVelocity, Is.EqualTo(0.5));
        Assert.That(config.Filtering.SmoothingWeight, Is.EqualTo(0.7));
        Assert.That(config.Filtering.MotionSigma, Is.EqualTo(2.0));
    }

    [Test]
    public async Task TestFilteringConfigurationIsAppliedToDevice()
    {
        // Arrange
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-filtering");
        Directory.CreateDirectory(workDir);
        var configLoader = new ConfigLoader(workDir);
        
        // Create a config with non-default values
        var config = new Config
        {
            Filtering = new ConfigFiltering
            {
                ProcessNoise = 0.123,
                MeasurementNoise = 0.456,
                MaxVelocity = 0.789,
                SmoothingWeight = 0.99,
                MotionSigma = 5.0
            }
        };
        
        var mqttMock = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        state.Config = config; // Manually set config
        
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history);

        var device = new Device("test_device", null, TimeSpan.FromSeconds(10));
        // Add a dummy scenario so it processes something
        device.Scenarios.Add(new Scenario(config, new AnchorLocator(new Point3D()), "test"));

        // Act
        await locator.ProcessDevice(device);

        // Assert - Verify Kalman Filter settings via Reflection
        var kf = device.KalmanFilter;
        var type = kf.GetType();
        
        var processNoise = (double)type.GetField("_processNoise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kf);
        var measurementNoise = (double)type.GetField("_measurementNoise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kf);
        var maxVelocity = (double)type.GetField("_maxVelocity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(kf);

        Assert.That(processNoise, Is.EqualTo(0.123));
        Assert.That(measurementNoise, Is.EqualTo(0.456));
        Assert.That(maxVelocity, Is.EqualTo(0.789));
    }
}
