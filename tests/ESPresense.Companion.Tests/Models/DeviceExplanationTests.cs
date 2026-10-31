using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class DeviceExplanationTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Floor _ground;
    private Floor _upstairs;
    private Device _device;

    [SetUp]
    public void Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _nodeTelemetryStore = new NodeTelemetryStore(new Mock<IMqttCoordinator>().Object);
        _state = new State(_configLoader, _nodeTelemetryStore);

        _ground = CreateFloor("ground", 0, 3,
            Room("Kitchen", 0, 0, 5, 10),
            Room("Hall", 5, 0, 10, 10));
        _upstairs = CreateFloor("upstairs", 3, 6);
        _device = new Device("phone", null, TimeSpan.FromSeconds(30));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _configLoader.StopAsync(CancellationToken.None);
        _configLoader.Dispose();
        await _nodeTelemetryStore.StopAsync(CancellationToken.None);
        _nodeTelemetryStore.Dispose();
        if (Directory.Exists(_configDir)) Directory.Delete(_configDir, true);
    }

    private Floor CreateFloor(string id, double minZ, double maxZ, params ConfigRoom[] rooms)
    {
        var floor = new Floor();
        floor.Update(_configLoader.Config!, new ConfigFloor
        {
            Id = id,
            Name = id,
            Bounds = [[0, 0, minZ], [10, 10, maxZ]],
            Rooms = rooms
        });
        _state.Floors[floor.Id] = floor;
        return floor;
    }

    private static ConfigRoom Room(string name, double x0, double y0, double x1, double y1) => new()
    {
        Name = name,
        Points = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
    };

    private DeviceToNode Hear(string id, Point3D location, double distance, Floor floor, bool stale = false)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = [location.X, location.Y, location.Z] }, [floor]);
        _state.Nodes[id] = node;
        var link = new DeviceToNode(_device, node)
        {
            Distance = distance,
            LastHit = DateTime.UtcNow - (stale ? TimeSpan.FromMinutes(5) : TimeSpan.Zero)
        };
        _device.Nodes[id] = link;
        return link;
    }

    private void HearAroundKitchen()
    {
        var truth = new Point3D(2, 5, 1);
        foreach (var (id, at) in new[]
                 {
                     ("a", new Point3D(0, 0, 1)), ("b", new Point3D(5, 0, 2)), ("c", new Point3D(0, 10, 2)),
                     ("d", new Point3D(5, 10, 1)), ("e", new Point3D(10, 5, 0.5))
                 })
            Hear(id, at, truth.DistanceTo(at), _ground);
    }

    private Scenario Run(ILocate locator, string? name = "ground")
    {
        var scenario = new Scenario(_state.Config, locator, name);
        _device.Scenarios.Add(scenario);
        scenario.Locate();
        return scenario;
    }

    private static ExplainedNode Node(ExplainedScenario scenario, string id) => scenario.Nodes.Single(n => n.Id == id);

    [Test]
    public void Explain_ReportsTheNodesLeftOutAndWhy()
    {
        HearAroundKitchen();
        Hear("stale", new Point3D(1, 1, 1), 1, _ground, stale: true);
        Hear("above", new Point3D(1, 1, 4), 1, _upstairs);
        _device.BestScenario = Run(new NelderMeadMultilateralizer(_device, _ground, _state));

        var scenario = DeviceExplanation.From(_device).Scenarios.Single();

        Assert.That(scenario.Outcome, Is.EqualTo(LocatorOutcome.Solved));
        Assert.That(scenario.Nodes.Count(n => n.Used), Is.EqualTo(5));
        Assert.That(Node(scenario, "stale").Reason, Is.EqualTo("Stale"));
        Assert.That(Node(scenario, "above").Reason, Is.EqualTo("NotOnFloor"));
        Assert.That(Node(scenario, "a").Weight, Is.GreaterThan(0));
    }

    [Test]
    public void Explain_ResidualIsPositiveForTheNodeReadingLong()
    {
        HearAroundKitchen();
        _device.Nodes["e"].Distance += 3;
        _device.BestScenario = Run(new BfgsMultilateralizer(_device, _ground, _state));

        var scenario = DeviceExplanation.From(_device).Scenarios.Single();

        var e = Node(scenario, "e");
        Assert.That(e.Residual, Is.GreaterThan(0));
        Assert.That(e.Residual, Is.EqualTo(e.Measured - e.Fitted).Within(1e-9));
        Assert.That(e.Fitted, Is.EqualTo(new Point3D(scenario.Fit!.Value.X, scenario.Fit.Value.Y, scenario.Fit.Value.Z)
            .DistanceTo(new Point3D(10, 5, 0.5))).Within(1e-9));
    }

    [Test]
    public void Explain_ResidualsAgainstTheDistancesTheLocatorUsed()
    {
        HearAroundKitchen();
        var solved = _device.Nodes["a"].Distance;
        _device.BestScenario = Run(new NelderMeadMultilateralizer(_device, _ground, _state));

        _device.Nodes["a"].Distance = 99; // arrives after the pass

        Assert.That(Node(DeviceExplanation.From(_device).Scenarios.Single(), "a").Measured, Is.EqualTo(solved));
    }

    [Test]
    public void Trace_AppliesScaleOnlyWhereTheFitDoes()
    {
        HearAroundKitchen();
        var nelderMead = Run(new NelderMeadMultilateralizer(_device, _ground, _state));
        var mle = Run(new MLEMultilateralizer(_device, _ground, _state));

        Assert.That(nelderMead.Trace!.Scale, Is.EqualTo(nelderMead.Scale));
        Assert.That(mle.Trace!.Scale, Is.EqualTo(1));
    }

    [Test]
    public void Explain_UnderThreeNodesIsAFallbackWithNoResiduals()
    {
        Hear("a", new Point3D(0, 0, 1), 2, _ground);
        Hear("b", new Point3D(4, 0, 1), 2, _ground);
        _device.BestScenario = Run(new NelderMeadMultilateralizer(_device, _ground, _state));

        var scenario = DeviceExplanation.From(_device).Scenarios.Single();

        Assert.That(scenario.Outcome, Is.EqualTo(LocatorOutcome.Fallback));
        Assert.That(scenario.OutcomeReason, Does.Contain("Under 3"));
        Assert.That(scenario.Nodes.All(n => n.Used && n.Residual == null), Is.True);
    }

    [TestCase(1)]
    [TestCase(0)]
    public void Explain_OneOrNoNodesIsNoFix(int heard)
    {
        if (heard == 1) Hear("a", new Point3D(0, 0, 1), 2, _ground);
        var nelderMead = Run(new NelderMeadMultilateralizer(_device, _ground, _state));
        var nadaraya = Run(new NadarayaWatsonMultilateralizer(_device, _ground, _state, _nodeTelemetryStore));
        var multiFloor = Run(new MultiFloorMultilateralizer(_device, _state), "MultiFloor");

        Assert.That(nelderMead.Trace!.Outcome, Is.EqualTo(LocatorOutcome.NoFix));
        Assert.That(nadaraya.Trace!.Outcome, Is.EqualTo(LocatorOutcome.NoFix));
        Assert.That(multiFloor.Trace!.Outcome, Is.EqualTo(heard == 0 ? LocatorOutcome.NoFix : LocatorOutcome.Fallback));
    }

    [Test]
    public async Task Explain_NearestNodeNamesTheLimit()
    {
        (await _configLoader.ConfigAsync()).Locators.NearestNode.MaxDistance = 5;
        Hear("near", new Point3D(0, 0, 1), 2, _ground);
        Hear("mid", new Point3D(4, 0, 1), 3, _ground);
        Hear("far", new Point3D(9, 0, 1), 8, _ground);
        _device.BestScenario = Run(new NearestNode(_device, _state), "NearestNode");

        var scenario = DeviceExplanation.From(_device).Scenarios.Single();

        Assert.That(scenario.Outcome, Is.EqualTo(LocatorOutcome.Fallback));
        Assert.That(Node(scenario, "near").Used, Is.True);
        Assert.That(Node(scenario, "mid").Reason, Is.EqualTo("NotNearest"));
        Assert.That(Node(scenario, "far").Reason, Is.EqualTo("OverMaxDistance"));
    }

    [Test]
    public void Label_SeparatesLocatorsOnTheSameFloorWithoutRenamingThem()
    {
        var nelderMead = new Scenario(null, new NelderMeadMultilateralizer(_device, _ground, _state), "Main Floor");
        var nadaraya = new Scenario(null, new NadarayaWatsonMultilateralizer(_device, _ground, _state, _nodeTelemetryStore), "Main Floor");
        var multiFloor = new Scenario(null, new MultiFloorMultilateralizer(_device, _state), "MultiFloor");

        Assert.That(nelderMead.Label, Is.EqualTo("Nelder-Mead (Main Floor)"));
        Assert.That(nadaraya.Label, Is.EqualTo("Nadaraya-Watson (Main Floor)"));
        Assert.That(multiFloor.Label, Is.EqualTo("MultiFloor"));
        Assert.That(nelderMead.Name, Is.EqualTo("Main Floor"));
    }

    [Test]
    public void GetDetails_ShowsRoomNamesAndKeepsScenariosApart()
    {
        HearAroundKitchen();
        _device.BestScenario = Run(new NelderMeadMultilateralizer(_device, _ground, _state));
        Run(new NadarayaWatsonMultilateralizer(_device, _ground, _state, _nodeTelemetryStore));

        var details = _device.GetDetails().ToList();

        Assert.That(details.Select(d => d.Key), Is.Unique);
        Assert.That(details.Single(d => d.Key == "Nelder-Mead (ground) Room").Value, Is.EqualTo("Kitchen"));
    }

    [Test]
    public void Explain_RoomDecisionGivesTheWallAndTheRoomBehindIt()
    {
        HearAroundKitchen();
        var scenario = new Scenario(_state.Config, new NelderMeadMultilateralizer(_device, _ground, _state), "ground")
        {
            Floor = _ground,
            Room = _ground.Rooms.Values.Single(r => r.Name == "Kitchen")
        };
        scenario.ResetLocation(new Point3D(4, 5, 1));
        _device.Scenarios.Add(scenario);
        _device.BestScenario = scenario;

        _device.ReportedState = "Hall";
        _device.ReportedState = "Kitchen";

        var room = DeviceExplanation.From(_device).Room;

        Assert.That(room.Room?.Name, Is.EqualTo("Kitchen"));
        Assert.That(room.Inside, Is.True);
        Assert.That(room.DistanceToEdge, Is.EqualTo(1).Within(1e-9));
        Assert.That(room.NearestOtherRoom?.Name, Is.EqualTo("Hall"));
        Assert.That(room.NearestOtherDistance, Is.EqualTo(1).Within(1e-9));
        Assert.That(room.State, Is.EqualTo("Kitchen"));
        Assert.That(room.PreviousState, Is.EqualTo("Hall"));
    }

    [Test]
    public void ReportedState_NullingToForceARepublishIsNotAStateChange()
    {
        _device.ReportedState = "Hall";
        _device.ReportedState = "Kitchen";
        _device.ReportedState = null;
        _device.ReportedState = "Kitchen";

        Assert.That(_device.PreviousReportedState, Is.EqualTo("Hall"));
    }

    [Test]
    public void Explain_FloorRaceSumsProbabilityPerFloor()
    {
        HearAroundKitchen();
        Hear("up1", new Point3D(1, 1, 4), 3, _upstairs);
        Hear("up2", new Point3D(9, 1, 4), 3, _upstairs);
        var nelderMead = Run(new NelderMeadMultilateralizer(_device, _ground, _state));
        var nadaraya = Run(new NadarayaWatsonMultilateralizer(_device, _ground, _state, _nodeTelemetryStore));
        var above = Run(new NelderMeadMultilateralizer(_device, _upstairs, _state), "upstairs");
        nelderMead.Probability = 0.4;
        nadaraya.Probability = 0.3;
        above.Probability = 0.3;
        _device.BestScenario = nelderMead;

        var explanation = DeviceExplanation.From(_device);

        Assert.That(explanation.Floors[0].Floor.Id, Is.EqualTo("ground"));
        Assert.That(explanation.Floors[0].Probability, Is.EqualTo(0.7).Within(1e-9));
        Assert.That(explanation.Scenarios[0].Best, Is.True);
    }

    [Test]
    public void Explain_AnchoredDeviceHasNoLocatorStory()
    {
        HearAroundKitchen();
        Run(new NelderMeadMultilateralizer(_device, _ground, _state));
        _device.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), _ground, null));

        var explanation = DeviceExplanation.From(_device);

        Assert.That(explanation.Anchored, Is.True);
        Assert.That(explanation.Scenarios, Is.Empty);
        Assert.That(explanation.Nodes, Has.Count.EqualTo(5));
    }

    [Test]
    public void Explain_SerializesWithTheApiJsonOptions()
    {
        HearAroundKitchen();
        _device.BestScenario = Run(new NelderMeadMultilateralizer(_device, _ground, _state));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(DeviceExplanation.From(_device), options);

        Assert.That(json, Does.Contain("\"outcome\":\"Solved\""));
        Assert.That(json, Does.Contain("\"residual\":"));
    }

    [Test]
    public void Explain_DeviceNotYetLocatedDoesNotThrow()
    {
        Hear("a", new Point3D(0, 0, 1), 2, _ground);
        _device.Scenarios.Add(new Scenario(_state.Config, new NelderMeadMultilateralizer(_device, _ground, _state), "ground"));

        var explanation = DeviceExplanation.From(_device);

        Assert.That(explanation.Scenarios.Single().Outcome, Is.Null);
        Assert.That(explanation.Room.Room, Is.Null);
        Assert.That(explanation.Location, Is.Null);
    }
}
