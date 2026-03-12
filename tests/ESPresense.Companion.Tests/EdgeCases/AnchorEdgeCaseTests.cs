using AutoMapper;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.EdgeCases;

[TestFixture]
public class AnchorEdgeCaseTests
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

    private Floor CreateTestFloor()
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Id = "test_floor",
            Name = "Test Floor",
            Bounds = new double[][]
            {
                new double[] { -10, -10, -10 },
                new double[] { 10, 10, 10 }
            }
        };
        floor.Update(_configLoader.Config!, configFloor);
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    [Test]
    public void AnchoredDevice_ConcurrentUpdates_ThreadSafety()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("concurrent-anchor", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        var tasks = new List<Task>();
        var exceptions = new List<Exception>();
        var successCount = 0;

        // Act - Perform 100 concurrent SetAnchor operations
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var settings = new DeviceSettings
                    {
                        Id = "concurrent-anchor",
                        OriginalId = "concurrent-anchor",
                        X = index % 10,
                        Y = index / 10,
                        Z = 0.0
                    };

                    _deviceSettingsStore.ApplyToDevice(device.Id, settings);

                    if (device.IsAnchored)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.That(exceptions, Is.Empty, "No exceptions should occur during concurrent updates");
        Assert.That(device.IsAnchored, Is.True, "Device should be anchored after concurrent updates");
        Assert.That(device.Anchor, Is.Not.Null);
        Assert.That(successCount, Is.GreaterThan(0), "At least some updates should succeed");
    }

    [Test]
    public void AnchoredDevice_TogglingAnchorRepeatedly_MaintainsConsistency()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("toggling-anchor", null, TimeSpan.FromSeconds(30));
        _state.Devices[device.Id] = device;

        var anchoredSettings = new DeviceSettings
        {
            Id = "toggling-anchor",
            OriginalId = "toggling-anchor",
            X = 5.0,
            Y = 5.0,
            Z = 0.0
        };

        var clearedSettings = new DeviceSettings
        {
            Id = "toggling-anchor",
            OriginalId = "toggling-anchor",
            X = null,
            Y = null,
            Z = null
        };

        // Act & Assert - Toggle 10 times
        for (int i = 0; i < 10; i++)
        {
            // Set anchor
            _deviceSettingsStore.ApplyToDevice(device.Id, anchoredSettings);
            Assert.That(device.IsAnchored, Is.True, $"Device should be anchored on iteration {i} (set)");
            Assert.That(device.Anchor, Is.Not.Null);
            Assert.That(device.Anchor!.Location.X, Is.EqualTo(5.0));
            Assert.That(device.Anchor!.Location.Y, Is.EqualTo(5.0));
            Assert.That(device.Anchor!.Location.Z, Is.EqualTo(0.0));

            // Clear anchor
            _deviceSettingsStore.ApplyToDevice(device.Id, clearedSettings);
            Assert.That(device.IsAnchored, Is.False, $"Device should not be anchored on iteration {i} (clear)");
            Assert.That(device.Anchor, Is.Null);
        }

        // Final set
        _deviceSettingsStore.ApplyToDevice(device.Id, anchoredSettings);
        Assert.That(device.IsAnchored, Is.True, "Device should be anchored after final set");
        Assert.That(device.Anchor, Is.Not.Null);
    }

    [Test]
    public void Calibration_AnchorWithNonAsciiName_HandlesCorrectly()
    {
        // Arrange
        var floor = CreateTestFloor();

        var node = new Node("node1", NodeSourceType.Config);
        var configNode = new ConfigNode { Name = "node1", Point = new double[] { 0, 0, 0 } };
        node.Update(_configLoader.Config!, configNode, new[] { floor });
        _state.Nodes[node.Id] = node;

        // Test various Unicode device names
        var unicodeNames = new[]
        {
            "家のキー",           // Japanese: "House keys"
            "Schlüssel",         // German: "Keys"
            "Ключи",             // Russian: "Keys"
            "مفاتيح",            // Arabic: "Keys"
            "🔑 Keys",           // Emoji
            "café_device",       // Accented characters
            "设备-123"            // Chinese with numbers
        };

        var devices = new List<Device>();

        foreach (var name in unicodeNames)
        {
            var device = new Device($"device_{unicodeNames.ToList().IndexOf(name)}", name, TimeSpan.FromSeconds(30));
            _state.Devices[device.Id] = device;

            var settings = new DeviceSettings
            {
                Id = device.Id,
                OriginalId = device.Id,
                Name = name,
                X = unicodeNames.ToList().IndexOf(name) * 1.0,
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

            devices.Add(device);
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
        Assert.That(calibration.Anchored.Count, Is.EqualTo(unicodeNames.Length));

        // Verify all Unicode names appear correctly in the matrix
        foreach (var name in unicodeNames)
        {
            var matchingKeys = calibration.Matrix.Keys.Where(k => k.Contains(name) || k == name).ToList();
            Assert.That(matchingKeys.Count, Is.GreaterThan(0), $"Unicode name '{name}' should appear in calibration matrix");
        }

        // Verify all devices are properly anchored
        foreach (var device in devices)
        {
            Assert.That(device.IsAnchored, Is.True, $"Device '{device.Name}' should be anchored");
        }
    }
}
