using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class StateAnchorTests
{
    private State _state;
    private string _configDir;
    private ConfigLoader _configLoader;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private DeviceSettingsStore _deviceSettingsStore;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);
        _deviceSettingsStore = new DeviceSettingsStore(_mockMqttCoordinator.Object, _state);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Test]
    public void GetScenarios_ReturnsAnchorScenarioForAnchoredDevice()
    {
        // Arrange
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(1.5, 2.5, 0.5);
        var anchor = new DeviceAnchor(anchorLocation, null, null);
        device.SetAnchor(anchor);

        // Act
        var scenarios = _state.GetScenarios(device).ToList();

        // Assert
        Assert.That(scenarios, Has.Count.EqualTo(1));
        var scenario = scenarios[0];
        Assert.That(scenario.Name, Is.EqualTo("Anchored"));
        Assert.That(scenario.Confidence, Is.EqualTo(100));
        Assert.That(scenario.Floor, Is.EqualTo(anchor.Floor));
        Assert.That(scenario.Room, Is.EqualTo(anchor.Room));
    }

    [Test]
    public void GetScenarios_ReturnsRegularScenariosForNonAnchoredDevice()
    {
        // Arrange
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        // Don't set an anchor

        // Act
        var scenarios = _state.GetScenarios(device).ToList();

        // Assert
        Assert.That(scenarios, Has.Count.GreaterThan(0));
        Assert.That(scenarios.All(s => s.Name != "Anchored"), Is.True);
    }

    [Test]
    public void GetScenarios_AnchorScenarioUsesAnchorLocator()
    {
        // Arrange
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(2.0, 3.0, 1.0);
        var anchor = new DeviceAnchor(anchorLocation, null, null);
        device.SetAnchor(anchor);

        // Act
        var scenarios = _state.GetScenarios(device).ToList();
        var scenario = scenarios[0];

        // Locate should set the device to the anchor location
        var moved = scenario.Locate();

        // Assert
        Assert.That(scenario.Location, Is.EqualTo(anchorLocation));
        Assert.That(scenario.Confidence, Is.EqualTo(100));
        Assert.That(moved, Is.True); // Should have moved from null to anchor location
    }

    [Test]
    public void GetScenarios_AnchorScenarioWithFloorAndRoom()
    {
        // Arrange
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(1.0, 1.0, 0.0);

        // Create floor and room using their Update methods
        var floor = new Floor();
        var configFloor = new ConfigFloor { Name = "Test Floor" };
        floor.Update(_configLoader.Config!, configFloor);

        var room = new Room();
        var configRoom = new ConfigRoom
        {
            Name = "Test Room",
            Points = new double[][]
            {
                new double[] { 0, 0 },
                new double[] { 5, 0 },
                new double[] { 5, 5 },
                new double[] { 0, 5 }
            }
        };
        room.Update(_configLoader.Config!, floor, configRoom);

        var anchor = new DeviceAnchor(anchorLocation, floor, room);
        device.SetAnchor(anchor);

        // Act
        var scenarios = _state.GetScenarios(device).ToList();

        // Assert
        Assert.That(scenarios, Has.Count.EqualTo(1));
        var scenario = scenarios[0];
        Assert.That(scenario.Floor, Is.EqualTo(floor));
        Assert.That(scenario.Room, Is.EqualTo(room));
    }
}