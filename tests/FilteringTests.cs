using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SQLite;
using YamlDotNet.Serialization;
using System.IO;
using System.Threading.Tasks;
using MathNet.Spatial.Euclidean;
using ESPresense.Locators;
using ESPresense.Controllers;

namespace ESPresense.Companion.Tests;

public class FilteringTests
{
    private class StaticLocator(Point3D location, int confidence = 100) : ILocate
    {
        public bool Locate(Scenario scenario)
        {
            scenario.Confidence = confidence;
            scenario.LastHit = DateTime.UtcNow;
            scenario.UpdateLocation(location);
            return true;
        }
    }

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
        timeout: 30
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
        var testId = TestContext.CurrentContext.Test.ID ?? Guid.NewGuid().ToString("N");
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"cfg-filtering-{testId}");
        Directory.CreateDirectory(workDir);

        // Write config to file
        var yaml = @"
filtering:
  process_noise: 0.123
  measurement_noise: 0.456
  max_velocity: 0.789
  smoothing_weight: 0.99
  motion_sigma: 5.0
";
        await File.WriteAllTextAsync(Path.Combine(workDir, "config.yaml"), yaml);
        var configLoader = new ConfigLoader(workDir);
        await configLoader.StartAsync(CancellationToken.None);

        try
        {
            await configLoader.ConfigAsync(); // Wait for load

            var mqttMock = new Mock<MqttCoordinator>(configLoader, NullLogger<MqttCoordinator>.Instance, new MqttNetLogger(), new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance));
            var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));

            var tele = new TelemetryService(mqttMock.Object);
            var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
            var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
            var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
            var leaseServiceMock = new Mock<ILeaseService>();
            var bayesianPublisher = new BayesianProbabilityPublisher(mqttMock.Object);
            var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object, bayesianPublisher);

            var device = new Device("test_device", null, TimeSpan.FromSeconds(10));
            device.KalmanFilter.Settings = state.KalmanSettings;
            // Add a dummy scenario so it processes something
            device.Scenarios.Add(new Scenario(state.Config, new StaticLocator(new Point3D(1, 2, 3)), "test"));

            // Act
            await locator.ProcessDevice(device);

            // Assert - Verify Kalman Filter settings come from config
            var kf = device.KalmanFilter.Settings;

            Assert.That(kf.ProcessNoise, Is.EqualTo(0.123));
            Assert.That(kf.MeasurementNoise, Is.EqualTo(0.456));
            Assert.That(kf.MaxVelocity, Is.EqualTo(0.789));
            Assert.That(device.Location?.X, Is.EqualTo(1));
        }
        finally
        {
            await configLoader.StopAsync(CancellationToken.None);
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, true);
        }
    }

    [Test]
    public void TestFilteringParametersAffectOutput()
    {
        // Scenario: We have a device at 0,0,0. It suddenly jumps to 10,0,0.
        // We want to compare a "Smooth" filter (trusts model, ignores noise) vs a "Responsive" filter (trusts measurement).

        var startPoint = new Point3D(0, 0, 0);
        var newPoint = new Point3D(10, 0, 0);

        var startTime = DateTime.UtcNow;

        // 1. Responsive Filter: High Process Noise (expects change), Low Measurement Noise (trusts data)
        var responsiveFilter = new KalmanLocation(new KalmanFilterSettings(1.0, 0.01, 100.0));
        responsiveFilter.Update(startPoint, startTime); // Initialize
        responsiveFilter.Update(newPoint, startTime.AddSeconds(1));
        var responsiveResult = responsiveFilter.Location;

        // 2. Smooth Filter: Low Process Noise (expects stability), High Measurement Noise (distrusts data)
        var smoothFilter = new KalmanLocation(new KalmanFilterSettings(0.001, 10.0, 100.0));
        smoothFilter.Update(startPoint, startTime); // Initialize
        smoothFilter.Update(newPoint, startTime.AddSeconds(1));
        var smoothResult = smoothFilter.Location;

        Console.WriteLine($"Responsive Result X: {responsiveResult.X}");
        Console.WriteLine($"Smooth Result X: {smoothResult.X}");

        // The responsive filter should have moved MUCH closer to 10 than the smooth filter
        Assert.That(responsiveResult.X, Is.GreaterThan(smoothResult.X));

        // Responsive should be very close to 10 (the measurement)
        Assert.That(responsiveResult.X, Is.GreaterThan(9.0));

        // Smooth should be closer to 0 (the previous state)
        Assert.That(smoothResult.X, Is.LessThan(2.0));
    }
}
