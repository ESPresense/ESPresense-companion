using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using AutoMapper;

namespace ESPresense.Companion.Tests.Controllers;

public class StateControllerAnchorTests
{
    private StateController _controller;
    private State _state;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<NodeSettingsStore> _mockNodeSettingsStore;
    private Mock<DeviceSettingsStore> _mockDeviceSettingsStore;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<StateController>> _mockLogger;
    private ConfigLoader _configLoader;
    private string _configDir;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _mockNodeSettingsStore = new Mock<NodeSettingsStore>(_mockMqttCoordinator.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<StateController>>();

        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        var nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _state = new State(_configLoader, nodeTelemetryStore);
        _mockDeviceSettingsStore = new Mock<DeviceSettingsStore>(_mockMqttCoordinator.Object, _state);

        var mockEventDispatcher = new Mock<GlobalEventDispatcher>();

        _controller = new StateController(
            _mockLogger.Object,
            _state,
            _configLoader,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object,
            nodeTelemetryStore,
            _mockMapper.Object,
            mockEventDispatcher.Object
        );
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
    public void GetCalibration_IncludesAnchoredDevicesInMatrix()
    {
        // Arrange
        // Create an anchored device
        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(1.0, 2.0, 0.5);
        var anchor = new DeviceAnchor(anchorLocation, null, null);
        device.SetAnchor(anchor);
        device.Name = "Test Anchor";

        // Add a receiving node
        var rxNode = new Node("rx-node", NodeSourceType.Config);
        var configNode = new ConfigNode { Name = "RX Node", Point = new double[] { 5.0, 2.0, 0.5 } };
        rxNode.Update(_configLoader.Config!, configNode, Enumerable.Empty<Floor>());
        _state.Nodes["rx-node"] = rxNode;

        // Add device-to-node measurement
        var deviceToNode = new DeviceToNode(device, rxNode)
        {
            Distance = 4.1,
            Rssi = -65,
            LastHit = DateTime.UtcNow,
            DistVar = 0.5
        };
        device.Nodes["rx-node"] = deviceToNode;

        // Add the device to state
        _state.Devices["anchored-device"] = device;

        // Setup node settings mock to return calibration data
        var nodeSettings = new NodeSettings { Calibration = new CalibrationSettings() };
        _mockNodeSettingsStore.Setup(x => x.Get("rx-node")).Returns(nodeSettings);

        // Act
        var calibration = _controller.GetCalibration();

        // Assert
        Assert.That(calibration.Matrix, Is.Not.Empty);
        Assert.That(calibration.Matrix.ContainsKey("Test Anchor"), Is.True,
            "Calibration matrix should include anchored device as transmitter");

        var anchorRow = calibration.Matrix["Test Anchor"];
        Assert.That(anchorRow.ContainsKey("RX Node"), Is.True,
            "Anchored device should have measurement to receiving node");

        var measurement = anchorRow["RX Node"];
        Assert.That(measurement["distance"], Is.EqualTo(4.1));
        Assert.That(measurement["rssi"], Is.EqualTo(-65));
        Assert.That(measurement["mapDistance"], Is.EqualTo(4.0).Within(0.01)); // Expected distance from anchor to rx node
        Assert.That(measurement["diff"], Is.EqualTo(0.1).Within(0.01)); // 4.1 - 4.0
    }

    [Test]
    public void GetCalibration_AnchoredDeviceDoesNotHaveTxRefRssi()
    {
        // Arrange
        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchor = new DeviceAnchor(new Point3D(0, 0, 0), null, null);
        device.SetAnchor(anchor);
        device.Name = "Test Anchor";

        var rxNode = new Node("rx-node", NodeSourceType.Config);
        var configNode = new ConfigNode { Point = new double[] { 1, 0, 0 } };
        rxNode.Update(_configLoader.Config!, configNode, Enumerable.Empty<Floor>());
        _state.Nodes["rx-node"] = rxNode;

        var deviceToNode = new DeviceToNode(device, rxNode) { LastHit = DateTime.UtcNow, Distance = 1.0, Rssi = -50 };
        device.Nodes["rx-node"] = deviceToNode;
        _state.Devices["anchored-device"] = device;

        var nodeSettings = new NodeSettings { Calibration = new CalibrationSettings { RxAdjRssi = -10 } };
        _mockNodeSettingsStore.Setup(x => x.Get("rx-node")).Returns(nodeSettings);

        // Act
        var calibration = _controller.GetCalibration();

        // Assert
        var anchorRow = calibration.Matrix["Test Anchor"];
        var measurement = anchorRow[rxNode.Name ?? rxNode.Id];

        // Anchored devices shouldn't have tx_ref_rssi (they're fixed reference points)
        Assert.That(measurement.ContainsKey("tx_ref_rssi"), Is.False);

        // But receiver should still have its calibration settings
        Assert.That(measurement["rx_adj_rssi"], Is.EqualTo(-10));
    }

    [Test]
    public void GetCalibration_AnchoredDevicesCanAppearAsReceivers()
    {
        // Arrange
        // Create a regular transmitter node
        var txNode = new Node("tx-node", NodeSourceType.Config);
        var configTxNode = new ConfigNode { Name = "TX Node", Point = new double[] { 0, 0, 0 } };
        txNode.Update(_configLoader.Config!, configTxNode, Enumerable.Empty<Floor>());
        _state.Nodes["tx-node"] = txNode;

        // Create an anchored device that might appear as a receiver
        var anchoredDevice = new Device("anchored-rx", null, TimeSpan.FromSeconds(30));
        var anchor = new DeviceAnchor(new Point3D(5, 5, 0), null, null);
        anchoredDevice.SetAnchor(anchor);
        anchoredDevice.Name = "Anchored RX";
        _state.Devices["anchored-rx"] = anchoredDevice;

        // Create a node-to-node relationship where the anchored device appears as RX
        var anchoredNode = _state.Nodes.GetOrAdd("anchored-rx", _ => new Node("anchored-rx", NodeSourceType.Discovered));
        var configAnchoredNode = new ConfigNode { Name = "Anchored RX", Point = new double[] { 5, 5, 0 } };
        anchoredNode.Update(_configLoader.Config!, configAnchoredNode, Enumerable.Empty<Floor>());

        var nodeToNode = new RxNode
        {
            Tx = txNode,
            Rx = anchoredNode,
            Distance = 5.0,
            Rssi = -70,
            LastHit = DateTime.UtcNow
        };
        txNode.RxNodes["anchored-rx"] = nodeToNode;

        // Setup node settings
        var txNodeSettings = new NodeSettings { Calibration = new CalibrationSettings { TxRefRssi = -60 } };
        var anchoredNodeSettings = new NodeSettings { Calibration = new CalibrationSettings() };
        _mockNodeSettingsStore.Setup(x => x.Get("tx-node")).Returns(txNodeSettings);
        _mockNodeSettingsStore.Setup(x => x.Get("anchored-rx")).Returns(anchoredNodeSettings);

        // Act
        var calibration = _controller.GetCalibration();

        // Assert
        Assert.That(calibration.Matrix.ContainsKey("TX Node"), Is.True, "TX Node should be in matrix as transmitter");

        var txRow = calibration.Matrix["TX Node"];
        Assert.That(txRow.ContainsKey("Anchored RX"), Is.True, "Anchored device should appear as receiver in data (UI can handle display)");
        Assert.That(txRow.Count, Is.EqualTo(1), "TX Node should have the anchored device as receiver");

        var anchoredRxMeasurement = txRow["Anchored RX"];
        Assert.That(anchoredRxMeasurement["distance"], Is.EqualTo(5.0));
        Assert.That(anchoredRxMeasurement["rssi"], Is.EqualTo(-70));
    }
}
