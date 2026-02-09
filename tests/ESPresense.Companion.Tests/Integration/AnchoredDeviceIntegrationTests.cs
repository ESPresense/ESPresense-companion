using AutoMapper;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;

namespace ESPresense.Companion.Tests.Integration;

[TestFixture]
public class AnchoredDeviceIntegrationTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
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
    public async Task TearDown()
    {
        if (_configLoader != null)
        {
            await _configLoader.StopAsync(CancellationToken.None);
            _configLoader.Dispose();
        }

        if (Directory.Exists(_configDir))
        {
            try
            {
                Directory.Delete(_configDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private Floor CreateTestFloor(string id, string name, double[] minBounds, double[] maxBounds)
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Id = id,
            Name = name,
            Bounds = new double[][] { minBounds, maxBounds }
        };
        floor.Update(_configLoader.Config!, configFloor);
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    private Room CreateTestRoom(string id, string name, Floor floor, double[][] points)
    {
        var room = new Room();
        var configRoom = new ConfigRoom
        {
            Id = id,
            Name = name,
            Points = points
        };
        room.Update(_configLoader.Config!, floor, configRoom);
        floor.Rooms[room.Id] = room;
        return room;
    }

    [Test]
    public void AnchoredDevice_WithMultipleFloors_SelectsCorrectFloor()
    {
        // Arrange - Create two floors at different Z levels
        var floor1 = CreateTestFloor("floor1", "Ground Floor",
            new double[] { -10, -10, -2 },
            new double[] { 10, 10, 2 });

        var floor2 = CreateTestFloor("floor2", "First Floor",
            new double[] { -10, -10, 3 },
            new double[] { 10, 10, 7 });

        // Create anchored device on Floor 1
        var device1 = new Device("anchor1", null, TimeSpan.FromSeconds(30));
        _state.Devices[device1.Id] = device1;

        var settings1 = new DeviceSettings
        {
            Id = "anchor1",
            OriginalId = "anchor1",
            X = 1.0,
            Y = 1.0,
            Z = 0.0  // Z=0 should match floor1 (bounds: -2 to 2)
        };

        // Create anchored device on Floor 2
        var device2 = new Device("anchor2", null, TimeSpan.FromSeconds(30));
        _state.Devices[device2.Id] = device2;

        var settings2 = new DeviceSettings
        {
            Id = "anchor2",
            OriginalId = "anchor2",
            X = 1.0,
            Y = 1.0,
            Z = 5.0  // Z=5 should match floor2 (bounds: 3 to 7)
        };

        // Act
        _deviceSettingsStore.ApplyToDevice(device1.Id, settings1);
        _deviceSettingsStore.ApplyToDevice(device2.Id, settings2);

        var scenarios1 = _state.GetScenarios(device1).ToList();
        var scenarios2 = _state.GetScenarios(device2).ToList();

        scenarios1[0].Locate();
        scenarios2[0].Locate();

        // Assert
        Assert.That(device1.IsAnchored, Is.True);
        Assert.That(device2.IsAnchored, Is.True);
        Assert.That(scenarios1[0].Floor, Is.EqualTo(floor1));
        Assert.That(scenarios2[0].Floor, Is.EqualTo(floor2));
    }

    [Test]
    public void AnchoredDevice_WithOverlappingRooms_SelectsCorrectRoom()
    {
        // Arrange
        _state.Floors.Clear();
        var floor = CreateTestFloor("floor1", "Test Floor",
            new double[] { -10, -10, -10 },
            new double[] { 10, 10, 10 });

        // Create two rooms that overlap slightly
        var room1 = CreateTestRoom("room1", "Living Room", floor, new double[][]
        {
            new double[] { 0, 0 },
            new double[] { 5, 0 },
            new double[] { 5, 5 },
            new double[] { 0, 5 }
        });

        var room2 = CreateTestRoom("room2", "Kitchen", floor, new double[][]
        {
            new double[] { 4.5, 0 },
            new double[] { 10, 0 },
            new double[] { 10, 5 },
            new double[] { 4.5, 5 }
        });

        // Create anchor clearly in room1
        var device = new Device("anchor", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        var settings = new DeviceSettings
        {
            Id = "anchor",
            OriginalId = "anchor",
            X = 2.0,  // Clearly in room1
            Y = 2.0,
            Z = 0.0
        };

        // Act
        _deviceSettingsStore.ApplyToDevice(device.Id, settings);
        var scenarios = _state.GetScenarios(device).ToList();
        scenarios[0].Locate();

        // Assert
        Assert.That(device.IsAnchored, Is.True);
        Assert.That(scenarios[0].Room, Is.EqualTo(room1));
    }

    [Test]
    public void GetCalibration_WithManyAnchoredDevices_PerformsWell()
    {
        // Arrange
        var floor = CreateTestFloor("floor1", "Test Floor",
            new double[] { -50, -50, -10 },
            new double[] { 50, 50, 10 });

        // Create node for calibration matrix
        var node = new Node("node1", NodeSourceType.Config);
        var configNode = new ConfigNode { Name = "node1", Point = new double[] { 0, 0, 0 } };
        node.Update(_configLoader.Config!, configNode, new[] { floor });
        _state.Nodes[node.Id] = node;

        // Create 50 anchored devices
        for (int i = 0; i < 50; i++)
        {
            var device = new Device($"anchor{i}", null, TimeSpan.FromSeconds(30));
            _state.Devices[device.Id] = device;

            var settings = new DeviceSettings
            {
                Id = $"anchor{i}",
                OriginalId = $"anchor{i}",
                X = (i % 10) * 5.0,
                Y = (i / 10) * 5.0,
                Z = 0.0
            };

            _deviceSettingsStore.ApplyToDevice(device.Id, settings);

            // Simulate device receiving signal from node
            device.Nodes[node.Id] = new DeviceToNode(device, node)
            {
                Distance = 5.0,
                Rssi = -60,
                LastHit = DateTime.UtcNow
            };
        }

        var mockNodeSettingsStore = new Mock<NodeSettingsStore>(_mockMqttCoordinator.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        var controller = new StateController(
            Mock.Of<ILogger<StateController>>(),
            _state,
            _configLoader,
            mockNodeSettingsStore.Object,
            _deviceSettingsStore,
            _nodeTelemetryStore,
            Mock.Of<IMapper>(),
            Mock.Of<GlobalEventDispatcher>()
        );

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var calibration = controller.GetCalibration();
        stopwatch.Stop();

        // Assert - Should complete in reasonable time (< 1 second)
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000));
        Assert.That(calibration.Anchored.Count, Is.EqualTo(50));
        Assert.That(calibration.Matrix.Count, Is.GreaterThanOrEqualTo(50)); // All anchors should be in matrix
    }

    [Test]
    public void MultipleAnchoredDevices_OnSameFloor_AllInCalibrationMatrix()
    {
        // Arrange
        var floor = CreateTestFloor("floor1", "Test Floor",
            new double[] { -10, -10, -10 },
            new double[] { 10, 10, 10 });

        var node = new Node("node1", NodeSourceType.Config);
        var configNode = new ConfigNode { Name = "node1", Point = new double[] { 5, 5, 0 } };
        node.Update(_configLoader.Config!, configNode, new[] { floor });
        _state.Nodes[node.Id] = node;

        var deviceIds = new[] { "anchor1", "anchor2", "anchor3" };
        foreach (var deviceId in deviceIds)
        {
            var device = new Device(deviceId, null, TimeSpan.FromSeconds(30));
            _state.Devices[device.Id] = device;

            var settings = new DeviceSettings
            {
                Id = deviceId,
                OriginalId = deviceId,
                X = deviceIds.ToList().IndexOf(deviceId) * 2.0,
                Y = 0.0,
                Z = 0.0
            };

            _deviceSettingsStore.ApplyToDevice(device.Id, settings);

            // Simulate receiving signal
            device.Nodes[node.Id] = new DeviceToNode(device, node)
            {
                Distance = 5.0,
                Rssi = -60,
                LastHit = DateTime.UtcNow
            };
        }

        var mockNodeSettingsStore = new Mock<NodeSettingsStore>(_mockMqttCoordinator.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        var controller = new StateController(
            Mock.Of<ILogger<StateController>>(),
            _state,
            _configLoader,
            mockNodeSettingsStore.Object,
            _deviceSettingsStore,
            _nodeTelemetryStore,
            Mock.Of<IMapper>(),
            Mock.Of<GlobalEventDispatcher>()
        );

        // Act
        var calibration = controller.GetCalibration();

        // Assert
        Assert.That(calibration.Anchored.Count, Is.EqualTo(3));
        Assert.That(calibration.Matrix.Keys, Contains.Item("anchor1"));
        Assert.That(calibration.Matrix.Keys, Contains.Item("anchor2"));
        Assert.That(calibration.Matrix.Keys, Contains.Item("anchor3"));
    }

    [Test]
    public void AnchoredDevice_CoordinateValidation_RejectsInvalidValues()
    {
        // Arrange
        var floor = CreateTestFloor("floor1", "Test Floor",
            new double[] { -10, -10, -10 },
            new double[] { 10, 10, 10 });

        var device = new Device("test-anchor", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        // Test cases with invalid coordinates
        var invalidSettings = new[]
        {
            new DeviceSettings { Id = "test", OriginalId = "test", X = double.NaN, Y = 0, Z = 0 },
            new DeviceSettings { Id = "test", OriginalId = "test", X = 0, Y = double.PositiveInfinity, Z = 0 },
            new DeviceSettings { Id = "test", OriginalId = "test", X = 0, Y = 0, Z = double.NegativeInfinity },
            new DeviceSettings { Id = "test", OriginalId = "test", X = 1e100, Y = 1e100, Z = 1e100 }
        };

        // Act & Assert
        foreach (var settings in invalidSettings)
        {
            _deviceSettingsStore.ApplyToDevice(device.Id, settings);

            // Device should either not be anchored or have valid fallback coordinates
            if (device.IsAnchored)
            {
                Assert.That(double.IsNaN(device.Anchor!.Location.X), Is.False);
                Assert.That(double.IsInfinity(device.Anchor!.Location.Y), Is.False);
                Assert.That(double.IsInfinity(device.Anchor!.Location.Z), Is.False);
            }
        }
    }

    [Test]
    public void AnchorLocator_WithFloors_AssignsCorrectFloorAndRoom()
    {
        // Arrange
        _state.Floors.Clear(); // Clear any floors from example config
        var floor = CreateTestFloor("floor1", "Test Floor",
            new double[] { -10, -10, -10 },
            new double[] { 10, 10, 10 });

        var room = CreateTestRoom("room1", "Test Room", floor, new double[][]
        {
            new double[] { 0, 0 },
            new double[] { 5, 0 },
            new double[] { 5, 5 },
            new double[] { 0, 5 }
        });

        var device = new Device("test-anchor", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        var settings = new DeviceSettings
        {
            Id = "test-anchor",
            OriginalId = "test-anchor",
            X = 2.5,  // Inside room polygon
            Y = 2.5,
            Z = 0.0
        };

        // Act
        _deviceSettingsStore.ApplyToDevice(device.Id, settings);
        var scenarios = _state.GetScenarios(device).ToList();
        scenarios[0].Locate();

        // Assert
        Assert.That(device.IsAnchored, Is.True);
        Assert.That(scenarios[0].Floor, Is.EqualTo(floor), "Floor should be assigned correctly");
        Assert.That(scenarios[0].Room, Is.EqualTo(room), "Room should be assigned correctly");
    }
}
