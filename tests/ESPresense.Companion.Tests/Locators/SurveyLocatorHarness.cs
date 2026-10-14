using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Locators;

/// <summary>
/// A one-floor <see cref="State"/> built from survey geometry, and a way to run a locator on a set
/// of per-node distances. Shared by the locator tests and the calibration tests, which both need to
/// ask "where would the system put the robot given these distances?".
/// </summary>
internal sealed class SurveyLocatorHarness
{
    private readonly State _state;
    private readonly ConfigLoader _configLoader;
    private readonly NodeTelemetryStore _nts;

    public Floor Floor { get; }
    public Dictionary<string, Node> Nodes { get; } = new();

    public SurveyLocatorHarness(State state, ConfigLoader configLoader, NodeTelemetryStore nts,
        double[][] floorBounds, IEnumerable<(string Id, double X, double Y, double Z)> nodes)
    {
        _state = state;
        _configLoader = configLoader;
        _nts = nts;

        Floor = new Floor();
        Floor.Update(configLoader.Config!, new ConfigFloor { Id = "first", Name = "First", Bounds = floorBounds });
        state.Floors[Floor.Id] = Floor;

        foreach (var n in nodes)
        {
            var node = new Node(n.Id, NodeSourceType.Config);
            node.Update(configLoader.Config!, new ConfigNode { Name = n.Id, Point = new[] { n.X, n.Y, n.Z } }, new[] { Floor });
            state.Nodes[n.Id] = node;
            Nodes[n.Id] = node;
        }
    }

    public ILocate Make(string which, Device d) => which switch
    {
        "nelder" => new NelderMeadMultilateralizer(d, Floor, _state),
        "mle" => new MLEMultilateralizer(d, Floor, _state),
        "gaussnewton" => new GaussNewtonMultilateralizer(d, Floor, _state),
        "bfgs" => new BfgsMultilateralizer(d, Floor, _state),
        "multifloor" => new MultiFloorMultilateralizer(d, _state),
        "nadaraya" => new NadarayaWatsonMultilateralizer(d, Floor, _state, _nts),
        "nearest" => new NearestNode(d, _state),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, null)
    };

    /// <summary>Runs one locator against one set of per-node distances; null if it produced no fix.</summary>
    public Point3D? Solve(string which, IEnumerable<KeyValuePair<string, double>> dists)
    {
        var device = new Device($"robot-{Guid.NewGuid()}", null, TimeSpan.FromSeconds(30));
        foreach (var (id, d) in dists)
            if (Nodes.TryGetValue(id, out var node))
                device.Nodes[id] = new DeviceToNode(device, node) { Distance = d, LastHit = DateTime.UtcNow };

        var loc = Make(which, device);
        var scenario = new Scenario(_configLoader.Config, loc, which);
        return loc.Locate(scenario) ? scenario.Location : null;
    }
}
