using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class BaseMultilateralizerTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mockMqttCoordinator;
    private Mock<NodeSettingsStore> _mockNodeSettingsStore;
    private Mock<DeviceSettingsStore> _mockDeviceSettingsStore;

    [SetUp]
    public void Setup()
    {
        _mockMqttCoordinator = new Mock<IMqttCoordinator>();
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);

        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(_mockMqttCoordinator.Object);
        _mockNodeSettingsStore = new Mock<NodeSettingsStore>(_mockMqttCoordinator.Object, null!);
        _mockDeviceSettingsStore = new Mock<DeviceSettingsStore>(_mockMqttCoordinator.Object, null!);
        var lazyDss = new Lazy<DeviceSettingsStore>(() => _mockDeviceSettingsStore.Object);
        _state = new State(_configLoader, _nodeTelemetryStore, _mockNodeSettingsStore.Object, lazyDss);
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

    private Floor CreateTestFloor(string id = "test_floor")
    {
        var floor = new Floor();
        var configFloor = new ConfigFloor
        {
            Id = id,
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

    private Node CreateTestNode(string id, double x, double y, double z, Floor floor, string? antenna = null)
    {
        var node = new Node(id, NodeSourceType.Config);
        // Set Id explicitly so ConfigNode.GetId() == id without ToSnakeCase transformation
        var configNode = new ConfigNode { Id = id, Name = id, Point = new double[] { x, y, z }, Antenna = antenna };
        // Ensure State.Config is non-null so EnrichNodes can look up antenna profile
        if (_state.Config == null)
            _state.Config = new Config();
        node.Update(_state.Config, configNode, new[] { floor });
        _state.Nodes[id] = node;
        // Register the ConfigNode in State.Config.Nodes so EnrichNodes can look up antenna profile
        _state.Config.Nodes = (_state.Config.Nodes ?? Array.Empty<ConfigNode>())
            .Append(configNode).ToArray();
        return node;
    }

    private TestMultilateralizer CreateTestMultilateralizer(Device device, Floor floor)
    {
        return new TestMultilateralizer(device, floor, _state, _mockNodeSettingsStore.Object, _mockDeviceSettingsStore.Object);
    }

    [Test]
    public void InitializeScenario_HandlesEmptyNodesArray()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Act
        var result = multilateralizer.PublicInitializeScenario(scenario, out var nodes, out var guess);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(nodes.Length, Is.EqualTo(0));
        Assert.That(double.IsNaN(guess.X), Is.True);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
    }

    [Test]
    public void InitializeScenario_HandlesSingleNode()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var node = CreateTestNode("node1", 1, 1, 0, floor);

        device.Nodes["node1"] = new DeviceToNode(device, node)
        {
            Distance = 1.0,
            LastHit = DateTime.UtcNow
        };

        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Act
        var result = multilateralizer.PublicInitializeScenario(scenario, out var nodes, out var guess);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(nodes.Length, Is.EqualTo(1));
        Assert.That(double.IsNaN(guess.X), Is.True);
        Assert.That(scenario.Confidence, Is.EqualTo(0));
        Assert.That(scenario.Room, Is.Null);
        Assert.That(scenario.Floor, Is.Null);
    }

    [Test]
    public void ClampToFloorBounds_HandlesNullBounds()
    {
        // Arrange
        var floor = new Floor();
        var configFloor = new ConfigFloor { Id = "unbounded_floor", Name = "Unbounded Floor" };
        floor.Update(_configLoader.Config!, configFloor);

        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var multilateralizer = CreateTestMultilateralizer(device, floor);
        var point = new Point3D(100, 200, 300);

        // Act
        var result = multilateralizer.PublicClampToFloorBounds(point);

        // Assert - point should be unchanged when no bounds
        Assert.That(result.X, Is.EqualTo(100));
        Assert.That(result.Y, Is.EqualTo(200));
        Assert.That(result.Z, Is.EqualTo(300));
    }

    [Test]
    public void ClampToFloorBounds_ClampsOutOfBoundsPoints()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Test point outside bounds (-10 to 10)
        var point = new Point3D(100, -200, 15);

        // Act
        var result = multilateralizer.PublicClampToFloorBounds(point);

        // Assert
        Assert.That(result.X, Is.EqualTo(10));   // Clamped to max
        Assert.That(result.Y, Is.EqualTo(-10));  // Clamped to min
        Assert.That(result.Z, Is.EqualTo(10));   // Clamped to max
    }

    [Test]
    public void CalculateAndSetPearsonCorrelation_HandlesLessThanTwoNodes()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        var nodes = new DeviceToNode[1];
        var node = CreateTestNode("node1", 1, 1, 0, floor);
        nodes[0] = new DeviceToNode(device, node) { Distance = 1.0 };

        // Act
        multilateralizer.PublicCalculateAndSetPearsonCorrelation(scenario, nodes);

        // Assert
        Assert.That(scenario.PearsonCorrelation, Is.Null);
    }

    [Test]
    public void HandleLocatorException_HandlesNaNGuess()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        var exception = new Exception("Test exception");
        var nanGuess = new Point3D(double.NaN, double.NaN, double.NaN);

        // Act
        var confidence = multilateralizer.PublicHandleLocatorException(exception, scenario, nanGuess);

        // Assert
        Assert.That(confidence, Is.EqualTo(0));
        // Should fallback to Point3D() which is (0, 0, 0)
        Assert.That(scenario.Location.X, Is.EqualTo(0));
        Assert.That(scenario.Location.Y, Is.EqualTo(0));
        Assert.That(scenario.Location.Z, Is.EqualTo(0));
    }

    [Test]
    public void FinalizeScenario_ReturnsFalseWhenNotMoved()
    {
        // Arrange
        var floor = CreateTestFloor();
        var device = new Device("test-device", null, TimeSpan.FromSeconds(30));
        var scenario = new Scenario(_configLoader.Config, CreateTestMultilateralizer(device, floor), "Test");
        var multilateralizer = CreateTestMultilateralizer(device, floor);

        // Set scenario location
        scenario.ResetLocation(new Point3D(5, 5, 0));
        scenario.LastLocation = new Point3D(5.05, 5.05, 0); // Less than 0.1 distance

        // Act
        var result = multilateralizer.PublicFinalizeScenario(scenario, 50);

        // Assert
        Assert.That(result, Is.False); // Should return false because movement < 0.1
    }

    // -----------------------------------------------------------------------
    // Tests verifying EnrichNodes correctly threads GMaxDb and antenna angles
    // through from config/calibration to DeviceToNode enrichment fields.
    // These cover: backward-compat null-GMaxDb fallback and az/el threading.
    //
    // NOTE: These tests are async so they can await ConfigLoader.ConfigAsync()
    // before running, ensuring the one-time asynchronous config load (which
    // fires State.ConfigChanged) has completed.  After that, we pin State.Config
    // to a known test Config so EnrichNodes sees deterministic data.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Helper: create 3 live nodes (non-zero RSSI) attached to <paramref name="device"/>
    /// and registered in <paramref name="floor"/>, WITHOUT modifying State.Config.Nodes.
    /// Callers are responsible for setting State.Config after the ConfigLoader race is resolved.
    /// </summary>
    private Device CreateDeviceWithThreeRssiNodes(Floor floor)
    {
        var device = new Device("dev", null, TimeSpan.FromSeconds(30));
        var nodeIds  = new[] { "n1", "n2", "n3" };
        var positions = new[] { (0.0, 0.0), (5.0, 0.0), (0.0, 5.0) };
        for (int k = 0; k < 3; k++)
        {
            // CreateTestNode registers in _state.Nodes and sets _state.Config.Nodes,
            // but we will overwrite State.Config after ConfigAsync() anyway.
            var node = CreateTestNode(nodeIds[k], positions[k].Item1, positions[k].Item2, 0, floor);
            var dn = new DeviceToNode(device, node)
            {
                Distance = 3.0,
                LastHit  = DateTime.UtcNow,
                Rssi     = -70.0,   // non-zero → EnrichNodes activates enrichment path
                RefRssi  = -59.0
            };
            device.Nodes[nodeIds[k]] = dn;
        }
        return device;
    }

    /// <summary>
    /// Pins State.Config to a fresh Config whose Nodes array is built from
    /// <paramref name="device"/>'s nodes with the given <paramref name="antenna"/> profile.
    /// Call AFTER awaiting ConfigAsync() to avoid ConfigChanged races.
    /// </summary>
    private void PinStateConfig(Device device, string? antenna)
    {
        _state.Config = new Config
        {
            Nodes = device.Nodes.Values
                .Select(dn => new ConfigNode { Id = dn.Node!.Id, Antenna = antenna })
                .ToArray()
        };
    }

    [Test]
    public async Task EnrichNodes_NullAntenna_SetsNodeGMaxDbToNull_BackwardCompatibility()
    {
        // Await initial config load so ConfigChanged has fired; no more fires after this.
        await _configLoader.ConfigAsync();

        // Arrange: nodes without antenna configured → NodeGMaxDb must remain null
        // so Distance falls back to firmware (_payloadDistance).
        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: null);   // sets State.Config with no antenna nodes

        _mockNodeSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new NodeSettings());
        _mockDeviceSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new DeviceSettings());

        var capturer = new CapturingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object);

        var scenario = new Scenario(_state.Config, capturer, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        // Act
        capturer.Locate(scenario);

        // Assert: every node should have NodeGMaxDb == null (backward-compat path)
        Assert.That(capturer.CapturedGMaxDb, Is.Not.Empty,
            "CapturingMultilateralizer should have captured enriched nodes");
        Assert.That(capturer.CapturedGMaxDb, Has.All.Null,
            "Nodes with null antenna must yield null NodeGMaxDb so " +
            "Distance falls back to firmware payload distance");
    }

    [Test]
    public async Task EnrichNodes_WithAntenna_SetsNodeGMaxDb()
    {
        // Await initial config load so ConfigChanged has fired; no more fires after this.
        await _configLoader.ConfigAsync();

        // Arrange: nodes WITH antenna=pcb_ifa configured → NodeGMaxDb must equal 3.4
        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");    // sets State.Config with pcb_ifa antenna nodes

        _mockNodeSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new NodeSettings());
        _mockDeviceSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new DeviceSettings());

        var capturer = new CapturingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object);

        var scenario = new Scenario(_state.Config, capturer, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        // Act
        capturer.Locate(scenario);

        // Assert: every node should have NodeGMaxDb == 3.4 (pcb_ifa profile)
        Assert.That(capturer.CapturedGMaxDb, Is.Not.Empty);
        Assert.That(capturer.CapturedGMaxDb, Has.All.EqualTo(3.4),
            "Nodes with antenna=pcb_ifa must yield NodeGMaxDb=3.4");
    }

    [Test]
    public async Task EnrichNodes_ReadsAzimuthElevationFromCalibration()
    {
        // Await initial config load so no ConfigChanged races.
        await _configLoader.ConfigAsync();

        // Arrange: calibration has Azimuth=45°, Elevation=30°.
        // After EnrichNodes these must be converted to radians and stored.
        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        var calSettings = new NodeSettings
        {
            Calibration = new CalibrationSettings { Azimuth = 45.0, Elevation = 30.0 }
        };
        _mockNodeSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(calSettings);
        _mockDeviceSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new DeviceSettings());

        var capturer = new CapturingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object);

        var scenario = new Scenario(_state.Config, capturer, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        // Act
        capturer.Locate(scenario);

        // Assert: azimuth and elevation converted correctly to radians
        double expectedAzRad = 45.0 * Math.PI / 180.0;
        double expectedElRad = 30.0 * Math.PI / 180.0;

        Assert.That(capturer.CapturedAzimuthRad, Is.Not.Empty);
        foreach (var az in capturer.CapturedAzimuthRad)
            Assert.That(az, Is.EqualTo(expectedAzRad).Within(1e-9),
                "NodeAzimuthRad must equal calibration azimuth (deg→rad)");
        foreach (var el in capturer.CapturedElevationRad)
            Assert.That(el, Is.EqualTo(expectedElRad).Within(1e-9),
                "NodeElevationRad must equal calibration elevation (deg→rad)");
    }

    [Test]
    public async Task EnrichNodes_DefaultAngles_WhenCalibrationMissing()
    {
        // Await initial config load so no ConfigChanged races.
        await _configLoader.ConfigAsync();

        // Arrange: no calibration → defaults are azimuth=0°, elevation=90°
        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        _mockNodeSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new NodeSettings()); // no calibration values
        _mockDeviceSettingsStore
            .Setup(x => x.Get(It.IsAny<string>()))
            .Returns(new DeviceSettings());

        var capturer = new CapturingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object);

        var scenario = new Scenario(_state.Config, capturer, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        // Act
        capturer.Locate(scenario);

        // Assert: default azimuth = 0 rad, default elevation = π/2 rad (90°)
        double defaultAzRad = 0.0 * Math.PI / 180.0;   // 0°
        double defaultElRad = 90.0 * Math.PI / 180.0;  // 90°

        Assert.That(capturer.CapturedAzimuthRad, Is.Not.Empty);
        foreach (var az in capturer.CapturedAzimuthRad)
            Assert.That(az, Is.EqualTo(defaultAzRad).Within(1e-9),
                "Default azimuth should be 0 degrees (0 rad)");
        foreach (var el in capturer.CapturedElevationRad)
            Assert.That(el, Is.EqualTo(defaultElRad).Within(1e-9),
                "Default elevation should be 90 degrees (π/2 rad)");
    }

    // -----------------------------------------------------------------------
    // Tests verifying the 3-iteration gain-correction loop in Locate().
    // -----------------------------------------------------------------------

    /// <summary>
    /// Solve() is called exactly 3 times when every iteration succeeds.
    /// </summary>
    [Test]
    public async Task Locate_CallsSolveExactlyThreeTimes()
    {
        await _configLoader.ConfigAsync();

        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        _mockNodeSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new NodeSettings());
        _mockDeviceSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new DeviceSettings());

        var counter = new CountingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object,
            returnNullOnIteration: -1); // never return null

        var scenario = new Scenario(_state.Config, counter, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        counter.Locate(scenario);

        Assert.That(counter.SolveCallCount, Is.EqualTo(3),
            "Solve() must be called exactly 3 times for the gain-correction loop");
    }

    /// <summary>
    /// On iteration 0 NodeCosTheta is null; on iterations 1 and 2 it is non-null
    /// (i.e. UpdateNodeCosTheta was invoked with the previous result).
    /// </summary>
    [Test]
    public async Task Locate_GainCorrection_IsNullOnIteration0_NonNullOnIterations1And2()
    {
        await _configLoader.ConfigAsync();

        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        _mockNodeSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new NodeSettings());
        _mockDeviceSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new DeviceSettings());

        var counter = new CountingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object,
            returnNullOnIteration: -1);

        var scenario = new Scenario(_state.Config, counter, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        counter.Locate(scenario);

        Assert.That(counter.SolveCallCount, Is.EqualTo(3));

        // Iteration 0: no cos(θ) computed yet → all nodes have NodeCosTheta == null
        Assert.That(counter.CosThetaPerIteration[0], Has.All.Null,
            "NodeCosTheta must be null on iteration 0 (no previous result to compute from)");

        // Iterations 1 and 2: UpdateNodeCosTheta was called → all nodes have a value
        Assert.That(counter.CosThetaPerIteration[1], Has.None.Null,
            "NodeCosTheta must be non-null on iteration 1 (cos(θ) computed from iteration-0 result)");
        Assert.That(counter.CosThetaPerIteration[2], Has.None.Null,
            "NodeCosTheta must be non-null on iteration 2 (cos(θ) computed from iteration-1 result)");
    }

    /// <summary>
    /// After Locate() finishes the loop successfully, enrichment fields on every node
    /// (NodeGMaxDb and NodeCosTheta) are reset to null.
    /// </summary>
    [Test]
    public async Task Locate_EnrichmentIsResetAfterSuccessfulLoop()
    {
        await _configLoader.ConfigAsync();

        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        _mockNodeSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new NodeSettings());
        _mockDeviceSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new DeviceSettings());

        var counter = new CountingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object,
            returnNullOnIteration: -1);

        var scenario = new Scenario(_state.Config, counter, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        counter.Locate(scenario);

        // After Locate() returns, ResetEnrichment() must have cleared gain fields
        var dnNodes = device.Nodes.Values.ToArray();
        Assert.That(dnNodes.Select(dn => dn.NodeGMaxDb), Has.All.Null,
            "NodeGMaxDb must be null after the loop (ResetEnrichment)");
        Assert.That(dnNodes.Select(dn => dn.NodeCosTheta), Has.All.Null,
            "NodeCosTheta must be null after the loop (ResetEnrichment)");
    }

    /// <summary>
    /// When Solve() returns null on a given iteration the loop exits early,
    /// ResetEnrichment is called, and Solve is not called again.
    /// </summary>
    [Test]
    public async Task Locate_NullSolveOnIteration1_StopsLoopAndResetsEnrichment()
    {
        await _configLoader.ConfigAsync();

        var floor  = CreateTestFloor();
        var device = CreateDeviceWithThreeRssiNodes(floor);
        PinStateConfig(device, antenna: "pcb_ifa");

        _mockNodeSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new NodeSettings());
        _mockDeviceSettingsStore.Setup(x => x.Get(It.IsAny<string>())).Returns(new DeviceSettings());

        // Return null on iteration 1 (0-indexed) so the loop stops after 2 calls
        var counter = new CountingMultilateralizer(
            device, floor, _state,
            _mockNodeSettingsStore.Object,
            _mockDeviceSettingsStore.Object,
            returnNullOnIteration: 1);

        var scenario = new Scenario(_state.Config, counter, "Test");
        scenario.ResetLocation(new Point3D(2, 2, 0));

        counter.Locate(scenario);

        Assert.That(counter.SolveCallCount, Is.EqualTo(2),
            "When Solve returns null on iteration 1 the loop must stop (only 2 calls total)");

        // Enrichment must still be reset even on early exit
        var dnNodes = device.Nodes.Values.ToArray();
        Assert.That(dnNodes.Select(dn => dn.NodeGMaxDb), Has.All.Null,
            "NodeGMaxDb must be null after early-exit (ResetEnrichment)");
        Assert.That(dnNodes.Select(dn => dn.NodeCosTheta), Has.All.Null,
            "NodeCosTheta must be null after early-exit (ResetEnrichment)");
    }

    /// <summary>
    /// Multilateralizer that counts Solve() calls and captures NodeCosTheta per iteration.
    /// Optionally returns null on a given iteration index to test early-exit behaviour.
    /// </summary>
    private class CountingMultilateralizer : BaseMultilateralizer
    {
        private readonly int _returnNullOnIteration;

        public int SolveCallCount { get; private set; }

        /// <summary>Snapshot of NodeCosTheta for every node at each Solve() call.</summary>
        public List<List<double?>> CosThetaPerIteration { get; } = new();

        public CountingMultilateralizer(
            Device device, Floor floor, State state,
            NodeSettingsStore nodeSettings, DeviceSettingsStore deviceSettings,
            int returnNullOnIteration)
            : base(device, floor, state, nodeSettings, deviceSettings)
        {
            _returnNullOnIteration = returnNullOnIteration;
        }

        protected override Point3D? Solve(Scenario scenario, DeviceToNode[] nodes, Point3D guess)
        {
            // Capture cos(θ) snapshot at this iteration
            CosThetaPerIteration.Add(nodes.Select(dn => dn.NodeCosTheta).ToList());

            if (SolveCallCount == _returnNullOnIteration)
            {
                SolveCallCount++;
                return null;
            }

            SolveCallCount++;
            // Return a fixed non-null point so UpdateNodeCosTheta can compute meaningful angles
            return new Point3D(2, 2, 0);
        }
    }

    // Test multilateralizer that exposes protected methods for testing
    private class TestMultilateralizer : BaseMultilateralizer
    {
        public TestMultilateralizer(Device device, Floor floor, State state, NodeSettingsStore nodeSettings, DeviceSettingsStore deviceSettings)
            : base(device, floor, state, nodeSettings, deviceSettings)
        {
        }

        /// <summary>
        /// Minimal Solve() implementation — always returns null so the base template
        /// falls back to the guess.  Tests exercise individual helper methods directly
        /// via the Public* wrappers below rather than going through Locate().
        /// </summary>
        protected override Point3D? Solve(Scenario scenario, DeviceToNode[] nodes, Point3D guess)
        {
            return null;
        }

        public bool PublicInitializeScenario(Scenario scenario, out DeviceToNode[] nodes, out Point3D guess)
        {
            return InitializeScenario(scenario, out nodes, out guess);
        }

        public Point3D PublicClampToFloorBounds(Point3D point)
        {
            return ClampToFloorBounds(point);
        }

        public void PublicCalculateAndSetPearsonCorrelation(Scenario scenario, DeviceToNode[] nodes)
        {
            CalculateAndSetPearsonCorrelation(scenario, nodes);
        }

        public int PublicHandleLocatorException(Exception ex, Scenario scenario, Point3D fallbackGuess)
        {
            return HandleLocatorException(ex, scenario, fallbackGuess);
        }

        public bool PublicFinalizeScenario(Scenario scenario, int confidence)
        {
            return FinalizeScenario(scenario, confidence);
        }
    }

    /// <summary>
    /// A multilateralizer that captures enrichment state on the first Solve() call
    /// so tests can verify what EnrichNodes stored without needing to expose the
    /// private method.  Returns a fixed non-null point so the iteration completes.
    /// </summary>
    private class CapturingMultilateralizer : BaseMultilateralizer
    {
        private int _callCount;

        /// <summary>NodeGMaxDb values captured at first Solve() call (iteration 0).</summary>
        public List<double?> CapturedGMaxDb      { get; } = new();
        /// <summary>NodeAzimuthRad values captured at first Solve() call.</summary>
        public List<double?> CapturedAzimuthRad  { get; } = new();
        /// <summary>NodeElevationRad values captured at first Solve() call.</summary>
        public List<double?> CapturedElevationRad { get; } = new();

        public CapturingMultilateralizer(
            Device device, Floor floor, State state,
            NodeSettingsStore nodeSettings,
            DeviceSettingsStore deviceSettings)
            : base(device, floor, state, nodeSettings, deviceSettings) { }

        protected override Point3D? Solve(Scenario scenario, DeviceToNode[] nodes, Point3D guess)
        {
            if (_callCount == 0)
            {
                // Capture a snapshot of enrichment fields before any cosTheta update.
                foreach (var dn in nodes)
                {
                    CapturedGMaxDb.Add(dn.NodeGMaxDb);
                    CapturedAzimuthRad.Add(dn.NodeAzimuthRad);
                    CapturedElevationRad.Add(dn.NodeElevationRad);
                }
            }
            _callCount++;
            // Return a non-null point so the 3-iteration loop completes normally
            // (prevents the null-path which calls ResetEnrichment before we capture).
            return new Point3D(1, 1, 0);
        }
    }
}
