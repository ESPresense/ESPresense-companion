using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Models;

public class StateOptimizationAnchorTests
{
    private State _state;
    private string _configDir;
    private ConfigLoader _configLoader;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);
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
    public void TakeOptimizationSnapshot_IncludesAnchoredDeviceMeasures()
    {
        // Arrange - Create nodes
        var node1 = new Node("node1", NodeSourceType.Config);
        var configNode1 = new ConfigNode { Name = "Node 1", Point = new double[] { 0, 0, 0 } };
        node1.Update(_configLoader.Config!, configNode1, Enumerable.Empty<Floor>());
        _state.Nodes["node1"] = node1;

        var node2 = new Node("node2", NodeSourceType.Config);
        var configNode2 = new ConfigNode { Name = "Node 2", Point = new double[] { 5, 0, 0 } };
        node2.Update(_configLoader.Config!, configNode2, Enumerable.Empty<Floor>());
        _state.Nodes["node2"] = node2;

        // Arrange - Create anchored device
        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(2.5, 2.5, 0);
        var anchor = new DeviceAnchor(anchorLocation, null, null);
        device.SetAnchor(anchor);
        _state.Devices["anchored-device"] = device;

        // Arrange - Add device-to-node measurements
        var deviceNode1 = new DeviceToNode(device, node1)
        {
            Rssi = -60,
            Distance = 3.5,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node1"] = deviceNode1;

        var deviceNode2 = new DeviceToNode(device, node2)
        {
            Rssi = -65,
            Distance = 4.2,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node2"] = deviceNode2;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // Assert - Should have measures from anchored device to nodes
        Assert.That(snapshot.Measures, Has.Count.GreaterThanOrEqualTo(2));

        var anchorMeasures = snapshot.Measures.Where(m => m.Tx.Id == device.Id).ToList();
        Assert.That(anchorMeasures, Has.Count.EqualTo(2), "Should have 2 measures from anchored device");

        // Verify measure to node1
        var measureToNode1 = anchorMeasures.FirstOrDefault(m => m.Rx.Id == node1.Id);
        Assert.That(measureToNode1, Is.Not.Null);
        Assert.That(measureToNode1!.Tx.Location, Is.EqualTo(anchorLocation), "Tx should be at anchor location");
        Assert.That(measureToNode1.Rx.Location, Is.EqualTo(node1.Location), "Rx should be at node1 location");
        Assert.That(measureToNode1.Rssi, Is.EqualTo(-60));
        Assert.That(measureToNode1.Distance, Is.EqualTo(3.5));
        Assert.That(measureToNode1.RefRssi, Is.EqualTo(-59));

        // Verify measure to node2
        var measureToNode2 = anchorMeasures.FirstOrDefault(m => m.Rx.Id == node2.Id);
        Assert.That(measureToNode2, Is.Not.Null);
        Assert.That(measureToNode2!.Tx.Location, Is.EqualTo(anchorLocation), "Tx should be at anchor location");
        Assert.That(measureToNode2.Rx.Location, Is.EqualTo(node2.Location), "Rx should be at node2 location");
        Assert.That(measureToNode2.Rssi, Is.EqualTo(-65));
        Assert.That(measureToNode2.Distance, Is.EqualTo(4.2));
    }

    [Test]
    public void TakeOptimizationSnapshot_OnlyIncludesCurrentMeasurements()
    {
        // Arrange
        var node1 = new Node("node1", NodeSourceType.Config);
        var configNode1 = new ConfigNode { Name = "Node 1", Point = new double[] { 0, 0, 0 } };
        node1.Update(_configLoader.Config!, configNode1, Enumerable.Empty<Floor>());
        _state.Nodes["node1"] = node1;

        var node2 = new Node("node2", NodeSourceType.Config);
        var configNode2 = new ConfigNode { Name = "Node 2", Point = new double[] { 5, 0, 0 } };
        node2.Update(_configLoader.Config!, configNode2, Enumerable.Empty<Floor>());
        _state.Nodes["node2"] = node2;

        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchor = new DeviceAnchor(new Point3D(2.5, 2.5, 0), null, null);
        device.SetAnchor(anchor);
        _state.Devices["anchored-device"] = device;

        // Current measurement
        var deviceNode1 = new DeviceToNode(device, node1)
        {
            Distance = 3.5,
            Rssi = -60,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node1"] = deviceNode1;

        // Stale measurement (not current) - LastHit is old
        var deviceNode2 = new DeviceToNode(device, node2)
        {
            Distance = 4.2,
            Rssi = -65,
            RefRssi = -59,
            LastHit = DateTime.UtcNow.AddMinutes(-10)
        };
        device.Nodes["node2"] = deviceNode2;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // Assert - Only current measurement should be included
        var anchorMeasures = snapshot.Measures.Where(m => m.Tx.Id == device.Id).ToList();
        Assert.That(anchorMeasures, Has.Count.EqualTo(1), "Should only include current measurements");
        Assert.That(anchorMeasures[0].Rx.Id, Is.EqualTo(node1.Id));
    }

    [Test]
    public void TakeOptimizationSnapshot_IgnoresNonAnchoredDevices()
    {
        // Arrange
        var node1 = new Node("node1", NodeSourceType.Config);
        var configNode1 = new ConfigNode { Name = "Node 1", Point = new double[] { 0, 0, 0 } };
        node1.Update(_configLoader.Config!, configNode1, Enumerable.Empty<Floor>());
        _state.Nodes["node1"] = node1;

        // Non-anchored device
        var device = new Device("regular-device", null, TimeSpan.FromSeconds(30));
        _state.Devices["regular-device"] = device;

        var deviceNode1 = new DeviceToNode(device, node1)
        {
            Distance = 3.5,
            Rssi = -60,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node1"] = deviceNode1;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // Assert - Regular devices should not appear as Tx in measures
        var deviceMeasures = snapshot.Measures.Where(m => m.Tx.Id == device.Id).ToList();
        Assert.That(deviceMeasures, Is.Empty, "Non-anchored devices should not be in optimization snapshot");
    }

    [Test]
    public void TakeOptimizationSnapshot_RequiresNodeWithLocation()
    {
        // Arrange
        var nodeWithLocation = new Node("node-with-location", NodeSourceType.Config);
        var configNode = new ConfigNode { Name = "Node With Location", Point = new double[] { 0, 0, 0 } };
        nodeWithLocation.Update(_configLoader.Config!, configNode, Enumerable.Empty<Floor>());
        _state.Nodes["node-with-location"] = nodeWithLocation;

        var nodeWithoutLocation = new Node("node-without-location", NodeSourceType.Discovered);
        // Don't call Update() - node will not have location
        _state.Nodes["node-without-location"] = nodeWithoutLocation;

        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchor = new DeviceAnchor(new Point3D(2.5, 2.5, 0), null, null);
        device.SetAnchor(anchor);
        _state.Devices["anchored-device"] = device;

        // Measurement to node with location
        var deviceNode1 = new DeviceToNode(device, nodeWithLocation)
        {
            Distance = 3.5,
            Rssi = -60,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node-with-location"] = deviceNode1;

        // Measurement to node without location
        var deviceNode2 = new DeviceToNode(device, nodeWithoutLocation)
        {
            Distance = 4.2,
            Rssi = -65,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["node-without-location"] = deviceNode2;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // Assert - Only node with location should be included
        var anchorMeasures = snapshot.Measures.Where(m => m.Tx.Id == device.Id).ToList();
        Assert.That(anchorMeasures, Has.Count.EqualTo(1), "Should only include nodes with locations");
        Assert.That(anchorMeasures[0].Rx.Id, Is.EqualTo(nodeWithLocation.Id));
    }

    [Test]
    public void TakeOptimizationSnapshot_MixesNodeToNodeAndAnchorMeasures()
    {
        // Arrange - Create transmitter and receiver nodes
        var txNode = new Node("tx-node", NodeSourceType.Config);
        var configTxNode = new ConfigNode { Name = "TX Node", Point = new double[] { 0, 0, 0 } };
        txNode.Update(_configLoader.Config!, configTxNode, Enumerable.Empty<Floor>());
        _state.Nodes["tx-node"] = txNode;

        var rxNode = new Node("rx-node", NodeSourceType.Config);
        var configRxNode = new ConfigNode { Name = "RX Node", Point = new double[] { 5, 0, 0 } };
        rxNode.Update(_configLoader.Config!, configRxNode, Enumerable.Empty<Floor>());
        _state.Nodes["rx-node"] = rxNode;

        // Add node-to-node measurement
        var rxFromTx = new RxNode
        {
            Tx = txNode,
            Rx = rxNode,
            Distance = 5.0,
            Rssi = -70,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        txNode.RxNodes["rx-node"] = rxFromTx;

        // Create anchored device
        var device = new Device("anchored-device", null, TimeSpan.FromSeconds(30));
        var anchor = new DeviceAnchor(new Point3D(2.5, 2.5, 0), null, null);
        device.SetAnchor(anchor);
        _state.Devices["anchored-device"] = device;

        var deviceNode = new DeviceToNode(device, rxNode)
        {
            Distance = 3.5,
            Rssi = -60,
            RefRssi = -59,
            LastHit = DateTime.UtcNow
        };
        device.Nodes["rx-node"] = deviceNode;

        // Act
        var snapshot = _state.TakeOptimizationSnapshot();

        // Assert - Should have both node-to-node and anchor measures
        Assert.That(snapshot.Measures, Has.Count.EqualTo(2));

        var nodeToNodeMeasure = snapshot.Measures.FirstOrDefault(m => m.Tx.Id == txNode.Id);
        Assert.That(nodeToNodeMeasure, Is.Not.Null, "Should have node-to-node measure");

        var anchorMeasure = snapshot.Measures.FirstOrDefault(m => m.Tx.Id == device.Id);
        Assert.That(anchorMeasure, Is.Not.Null, "Should have anchor measure");
    }
}
