using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Moq;
using Newtonsoft.Json.Linq;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class PositionAccuracyTests
{
    private State _state;
    private ConfigLoader _configLoader;
    private string _configDir;
    private NodeTelemetryStore _nodeTelemetryStore;
    private Mock<IMqttCoordinator> _mqtt;
    private Floor _floor;

    [SetUp]
    public async Task Setup()
    {
        _configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
        _configLoader = new ConfigLoader(_configDir);
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.SetupGet(m => m.DiscoveryTopic).Returns("homeassistant");
        _nodeTelemetryStore = new NodeTelemetryStore(_mqtt.Object);
        _state = new State(_configLoader, _nodeTelemetryStore);

        // The first config load brings in the example nodes; these tests place their own
        await _configLoader.ConfigAsync();
        _state.Nodes.Clear();

        _floor = new Floor();
        _floor.Update(_configLoader.Config!, new ConfigFloor { Id = "ground", Name = "ground", Bounds = [[0, 0, 0], [20, 20, 3]] });
        _state.Floors[_floor.Id] = _floor;
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

    private Node Place(string id, double x, double y, double z = 1.5, bool stationary = true)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(_configLoader.Config!, new ConfigNode { Id = id, Name = id, Point = [x, y, z], Stationary = stationary }, [_floor]);
        _state.Nodes[id] = node;
        return node;
    }

    private static void Link(Node tx, Node rx, double distance)
    {
        var link = new RxNode { Tx = tx, Rx = rx, Distance = distance, LastHit = DateTime.UtcNow };
        tx.RxNodes[rx.Id] = link;
    }

    private Node[] Network()
    {
        var nodes = new[]
        {
            Place("a", 2, 2, 1), Place("b", 18, 2, 2), Place("c", 2, 18, 2), Place("d", 18, 18, 1),
            Place("e", 10, 4, 1), Place("f", 10, 16, 2), Place("g", 4, 10, 1.5), Place("h", 16, 10, 1)
        };
        return nodes;
    }

    private static void LinkAll(Node[] nodes, Func<Node, Point3D>? truth = null)
    {
        foreach (var tx in nodes)
            foreach (var rx in nodes.Where(n => n != tx))
                Link(tx, rx, (truth?.Invoke(tx) ?? tx.Location).DistanceTo(truth?.Invoke(rx) ?? rx.Location));
    }

    [Test]
    public void Measure_FindsNodesWhereTheyAreWhenDistancesAreTrue()
    {
        LinkAll(Network());

        var accuracy = PositionAccuracy.Measure(_state);

        Assert.That(accuracy.Solved, Is.EqualTo(8));
        Assert.That(accuracy.Unsolved, Is.Zero);
        Assert.That(accuracy.Median, Is.LessThan(0.5));
        Assert.That(accuracy.Guidance, Does.StartWith("Good"));
    }

    [Test]
    public void Measure_PointsAtTheNodeThatIsMisplacedOnTheMap()
    {
        var nodes = Network();
        var misplaced = nodes.Single(n => n.Id == "e");
        LinkAll(nodes, n => n == misplaced ? new Point3D(10, 12, 1) : n.Location); // really 8 m from its pin

        var accuracy = PositionAccuracy.Measure(_state);

        Assert.That(accuracy.Worst?.Id, Is.EqualTo("e"));
        Assert.That(accuracy.Worst?.Error, Is.EqualTo(8).Within(2));
        Assert.That(accuracy.Guidance, Does.StartWith("Start with e"));

        // One bad pin hurts its neighbours' tests too, so the largest error isn't the culprit's
        Assert.That(accuracy.Nodes[0].Id, Is.Not.EqualTo("e"));
    }

    [Test]
    public void Measure_NeedsThreeNeighbours()
    {
        var nodes = new[] { Place("a", 2, 2), Place("b", 18, 2), Place("c", 2, 18) };
        LinkAll(nodes);

        var accuracy = PositionAccuracy.Measure(_state);

        Assert.That(accuracy.Placed, Is.EqualTo(3));
        Assert.That(accuracy.Solved, Is.Zero);
        Assert.That(accuracy.Median, Is.Null);
        Assert.That(accuracy.Worst, Is.Null);
        Assert.That(accuracy.Guidance, Does.StartWith("Needs nodes"));
    }

    [Test]
    public void Measure_SkipsMovingNodesAndLeavesOptimizerHistoryAlone()
    {
        var nodes = Network().Append(Place("mobile", 5, 5, stationary: false)).ToArray();
        LinkAll(nodes);

        var accuracy = PositionAccuracy.Measure(_state);

        Assert.That(accuracy.Nodes.Select(n => n.Id), Has.None.EqualTo("mobile"));
        Assert.That(_state.OptimizationSnaphots, Is.Empty);
    }

    [Test]
    public async Task RunOnce_PublishesASensorWithTheMedianAndWorstNode()
    {
        LinkAll(Network());
        var published = new Dictionary<string, string?>();
        _mqtt.Setup(m => m.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), true))
            .Callback<string, string?, bool>((topic, payload, _) => published[topic] = payload)
            .Returns(Task.CompletedTask);

        await new PositionAccuracyService(_state, _mqtt.Object).RunOnce();

        var config = JObject.Parse(published["homeassistant/sensor/espresense_companion_position_accuracy/config"]!);
        Assert.That(config["state_topic"]!.Value<string>(), Is.EqualTo(PositionAccuracyService.StateTopic));
        Assert.That(config["unit_of_measurement"]!.Value<string>(), Is.EqualTo("m"));
        Assert.That(double.Parse(published[PositionAccuracyService.StateTopic]!), Is.LessThan(0.5));
        Assert.That(JObject.Parse(published[PositionAccuracyService.DetailsTopic]!)["worst_node"], Is.Not.Null);
    }

    [Test]
    public void Topics_AreNotMistakenForDeviceAttributes()
    {
        Assert.That(PositionAccuracyService.DetailsTopic, Does.Not.Match("^espresense/companion/[^/]+/attributes$"));
    }
}
