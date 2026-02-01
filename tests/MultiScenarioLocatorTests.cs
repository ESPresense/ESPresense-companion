using ESPresense.Controllers;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using MathNet.Spatial.Euclidean;
using SQLite;

namespace ESPresense.Companion.Tests;

public class MultiScenarioLocatorTests
{
    private class DummyLocator : ILocate
    {
        public bool Locate(Scenario scenario) => true;
    }

    private class FixedRoomLocator(Room room, int confidence, double probability = 1.0) : ILocate
    {
        private readonly Room _room = room;
        private readonly int _confidence = confidence;
        private readonly double _probability = probability;

        public bool Locate(Scenario scenario)
        {
            scenario.Room = _room;
            scenario.Floor = _room.Floor;
            scenario.Confidence = _confidence;
            scenario.Probability = _probability;
            scenario.Fixes = (scenario.Fixes ?? 0) + 1;
            scenario.UpdateLocation(new Point3D());
            return true;
        }
    }

    [Test]
    public async Task NotHomeStateWhenAllScenariosExpire()
    {
        // Arrange minimal environment
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg");
        Directory.CreateDirectory(workDir);

        var configLoader = new ConfigLoader(workDir);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);

        var mqttMock = new Mock<MqttCoordinator>(configLoader,
            NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(),
            supervisor);

        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
        var leaseServiceMock = new Mock<ILeaseService>();
        var bayesianPublisher = new BayesianProbabilityPublisher(mqttMock.Object);

        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object, bayesianPublisher);

        var device = new Device("id", null, TimeSpan.FromSeconds(1))
        {
            ReportedState = "kitchen"
        };

        var scenario = new Scenario(null, new DummyLocator(), "kitchen")
        {
            LastHit = DateTime.UtcNow.AddSeconds(-5)
        };
        device.Scenarios.Add(scenario);

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", "not_home", false))
                .Returns(Task.CompletedTask)
                .Verifiable();

        // Act
        await locator.ProcessDevice(device);

        // Assert
        Assert.That(device.ReportedState, Is.EqualTo("not_home"));
        mqttMock.Verify();
    }

    [Test]
    public async Task BayesianProbabilitiesPublishedWhenEnabled()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString());
        Directory.CreateDirectory(workDir);

        var configPath = Path.Combine(workDir, "config.yaml");
        await File.WriteAllTextAsync(configPath,
            """
            mqtt:
              host:
            timeout: 30
            bayesian_probabilities:
              enabled: true
              discovery_threshold: 0.1
              retain: true
            locators:
              nadaraya_watson:
                enabled: false
              nelder_mead:
                enabled: false
              nearest_node:
                enabled: false
            floors:
              - id: f1
                name: Floor 1
                bounds: [[0,0,0],[5,5,3]]
                rooms:
                  - name: Kitchen
                    points: [[0,0],[0,1],[1,1],[1,0]]
                  - name: Hall
                    points: [[1,0],[1,1],[2,1],[2,0]]
            nodes: []
            devices: []
            """);

        var configLoader = new ConfigLoader(workDir);
        await configLoader.ConfigAsync();
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);

        var mqttMock = new Mock<MqttCoordinator>(configLoader,
            NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(),
            supervisor);

        var published = new List<(string topic, string? payload, bool retain)>();
        mqttMock
            .Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns<string, string?, bool>((topic, payload, retain) =>
            {
                published.Add((topic, payload, retain));
                return Task.CompletedTask;
            });

        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
        var leaseServiceMock = new Mock<ILeaseService>();
        var bayesianPublisher = new BayesianProbabilityPublisher(mqttMock.Object);
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object, bayesianPublisher);

        var config = await configLoader.ConfigAsync();
        var floor = state.Floors.Values.First();
        var rooms = floor.Rooms.Values.ToArray();
        var kitchen = rooms.First(r => r.Name == "Kitchen");
        var hall = rooms.First(r => r.Name == "Hall");

        var device = new Device("device1", null, TimeSpan.FromSeconds(30)) { Name = "Device" };
        device.Scenarios.Add(new Scenario(config, new FixedRoomLocator(kitchen, 80), kitchen.Name));
        device.Scenarios.Add(new Scenario(config, new FixedRoomLocator(hall, 60), hall.Name));

        await locator.ProcessDevice(device);

        // Assert NO separate probability topics published
        var hasProbabilityTopics = published.Any(p => p.topic.Contains("/probabilities/"));
        Assert.That(hasProbabilityTopics, Is.False, "No separate probability topics should be published");

        // Assert discovery config published for rooms above threshold with correct template
        var discoveryMessages = published.Where(p => p.topic.Contains("homeassistant/sensor/")).ToList();
        Assert.That(discoveryMessages, Is.Not.Empty, "Discovery messages should be published for rooms above threshold");

        var kitchenDiscovery = discoveryMessages.FirstOrDefault(m => m.payload?.Contains("Kitchen Probability") == true);
        Assert.That(kitchenDiscovery.payload, Is.Not.Null);
        var kitchenObj = JObject.Parse(kitchenDiscovery.payload!);
        Assert.That(kitchenObj["state_topic"]?.ToString(), Is.EqualTo($"espresense/companion/{device.Id}/attributes"));
        Assert.That(kitchenObj["value_template"]?.ToString(), Is.EqualTo("{{ value_json.probabilities['kitchen'] | default(0) }}"));

        var attributesMessage = published.LastOrDefault(p => p.topic == $"espresense/companion/{device.Id}/attributes");
        Assert.That(attributesMessage.payload, Is.Not.Null);

        var attributes = JObject.Parse(attributesMessage.payload!);
        var probabilities = attributes["probabilities"] as JObject;
        Assert.That(probabilities, Is.Not.Null);

        // Check that probabilities exist and are valid (case-insensitive check since room names might vary)
        var probabilityKeys = probabilities!.Properties().Select(p => p.Name).ToList();
        Assert.That(probabilityKeys.Count, Is.GreaterThan(0), $"Expected probabilities but got keys: {string.Join(", ", probabilityKeys)}");

        // Find kitchen and hall probabilities (case-insensitive)
        var kitchenProb = probabilities.Properties().FirstOrDefault(p => p.Name.Equals("Kitchen", StringComparison.OrdinalIgnoreCase))?.Value.Value<double>();
        var hallProb = probabilities.Properties().FirstOrDefault(p => p.Name.Equals("Hall", StringComparison.OrdinalIgnoreCase))?.Value.Value<double>();

        Assert.That(kitchenProb, Is.Not.Null, $"Kitchen probability not found. Available keys: {string.Join(", ", probabilityKeys)}");
        Assert.That(hallProb, Is.Not.Null, $"Hall probability not found. Available keys: {string.Join(", ", probabilityKeys)}");
        Assert.That(kitchenProb, Is.GreaterThan(0));
        Assert.That(hallProb, Is.GreaterThan(0));

        // Test probability drop stays "sticky" (discovery NOT deleted for existing rooms)
        device.Scenarios.Clear();
        // Kitchen stays, but drops below threshold (0.01 / 1.01 = ~0.009 << 0.1)
        device.Scenarios.Add(new Scenario(config, new FixedRoomLocator(kitchen, 80, 0.01), kitchen.Name));
        // Hall stays above threshold (1.0 / 1.01 = ~0.99 >> 0.1)
        device.Scenarios.Add(new Scenario(config, new FixedRoomLocator(hall, 60, 1.0), hall.Name));
        published.Clear();
        await locator.ProcessDevice(device);

        var kitchenDelete = published.Any(p => p.topic.Contains("kitchen/config") && p.payload == null);
        Assert.That(kitchenDelete, Is.False, "Kitchen discovery should NOT be deleted even though probability dropped");

        // Test untracking STILL deletes discovery entities
        device.Scenarios.Clear();
        published.Clear();
        await locator.ProcessDevice(device);

        var deleteMessages = published.Where(p => p.topic.Contains("homeassistant/sensor/") && p.payload == null).ToArray();
        Assert.That(deleteMessages, Is.Not.Empty, "Discovery delete messages should be sent when untracking");
    }

    [Test]
    public async Task AnchoredDevicePublishesFixedAttributes()
    {
        var workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg-anchor");
        Directory.CreateDirectory(workDir);

        var configLoader = new ConfigLoader(workDir);
        var supervisor = new SupervisorConfigLoader(NullLogger<SupervisorConfigLoader>.Instance);

        var mqttMock = new Mock<MqttCoordinator>(configLoader,
            NullLogger<MqttCoordinator>.Instance,
            new MqttNetLogger(),
            supervisor)
        { CallBase = true };

        var state = new State(configLoader, new NodeTelemetryStore(mqttMock.Object));
        var tele = new TelemetryService(mqttMock.Object);
        var deviceSettingsStore = new DeviceSettingsStore(mqttMock.Object, state);
        var tracker = new DeviceTracker(state, mqttMock.Object, tele, new GlobalEventDispatcher(), deviceSettingsStore);
        var history = new DeviceHistoryStore(new SQLiteAsyncConnection(":memory:"), configLoader);
        var leaseServiceMock = new Mock<ILeaseService>();
        var bayesianPublisher = new BayesianProbabilityPublisher(mqttMock.Object);
        var locator = new MultiScenarioLocator(tracker, state, mqttMock.Object, new GlobalEventDispatcher(), history, leaseServiceMock.Object, bayesianPublisher);

        var device = new Device("anchored", null, TimeSpan.FromSeconds(1));
        var anchorLocation = new Point3D(5, 6, 1);
        device.SetAnchor(new DeviceAnchor(anchorLocation, null, null));
        state.Devices[device.Id] = device;

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}", "not_home", false))
            .Returns(Task.CompletedTask)
            .Verifiable();

        mqttMock.Setup(m => m.EnqueueAsync($"espresense/companion/{device.Id}/attributes", It.IsAny<string>(), true))
            .Returns(Task.CompletedTask)
            .Verifiable();

        await locator.ProcessDevice(device);

        Assert.That(device.ReportedLocation, Is.EqualTo(anchorLocation));
        Assert.That(device.BestScenario, Is.Null);

        mqttMock.Verify();
    }
}
