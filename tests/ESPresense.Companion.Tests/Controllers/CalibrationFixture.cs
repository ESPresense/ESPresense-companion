using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Controllers;
using ESPresense.Models;
using ESPresense.Services;
using MathNet.Spatial.Euclidean;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Controllers;

/// <summary>
/// Synthetic state with node-to-node pairs and anchored devices, shared by the calibration
/// characterization tests (REST and MCP). All ids and names are made up.
/// </summary>
public sealed class CalibrationFixture : IAsyncDisposable
{
    public State State { get; }
    public ConfigLoader ConfigLoader { get; }
    public Mock<IMqttCoordinator> Mqtt { get; } = new();
    public Mock<NodeSettingsStore> Nsd { get; }
    public DeviceSettingsStore DeviceSettings { get; }
    public NodeTelemetryStore NodeTelemetry { get; }
    private readonly string _configDir;

    /// <summary>The options REST responses use: the same <see cref="global::ESPresense.Utils.JsonDefaults.Web"/> Program.cs applies.</summary>
    public static readonly JsonSerializerOptions RestJson = global::ESPresense.Utils.JsonDefaults.Web;

    private CalibrationFixture(string configDir)
    {
        _configDir = configDir;
        ConfigLoader = new ConfigLoader(configDir);
        NodeTelemetry = new NodeTelemetryStore(Mqtt.Object);
        State = new State(ConfigLoader, NodeTelemetry);
        Nsd = new Mock<NodeSettingsStore>(Mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>()) { CallBase = true };
        DeviceSettings = new DeviceSettingsStore(Mqtt.Object, State);
    }

    public static async Task<CalibrationFixture> CreateAsync()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var f = new CalibrationFixture(dir);
        await f.ConfigLoader.LoadAsync();
        f.Populate();
        return f;
    }

    private Node AddNode(string id, string name, double x, double y, double z, bool stationary = true)
    {
        var node = new Node(id, NodeSourceType.Config);
        node.Update(ConfigLoader.Config!, new ConfigNode { Name = name, Point = new[] { x, y, z }, Stationary = stationary }, Enumerable.Empty<Floor>());
        State.Nodes[id] = node;
        return node;
    }

    private static void Link(Node tx, Node rx, double distance, double rssi, double? distVar, DateTime? lastHit = null)
        => tx.RxNodes[rx.Id] = new RxNode { Tx = tx, Rx = rx, Distance = distance, Rssi = rssi, DistVar = distVar, LastHit = lastHit ?? DateTime.UtcNow };

    private void Populate()
    {
        var a = AddNode("node-a", "Alpha", 0, 0, 1);
        var b = AddNode("node-b", "Bravo", 4, 0, 1);
        var c = AddNode("node-c", "Charlie", 0, 3, 1);
        // A node on a car: it has a point, but stationary: false says it is only sometimes there.
        var d = AddNode("node-d", "Delta", -2, 15, 0.5, stationary: false);

        // Full mesh, with a NaN variance, a missing variance and a stale link that must be skipped.
        Link(a, b, 4.5, -71, 0.25);
        Link(a, c, 2.5, -66, null);
        Link(b, a, 3.75, -70, 0.5);
        Link(b, c, 5.5, -78, double.NaN);
        Link(c, a, 3.25, -64, 0.125);
        Link(c, b, 4.75, -77, 1.0);
        Link(b, b, 9, -90, null, DateTime.UtcNow.AddHours(-1));
        // Delta also exchanges node-to-node telemetry directly with a fixed node, both as tx and as rx; as a
        // non-stationary node it has no fixed distance either way, so neither pair gets a cell or an R/RMSE pair.
        Link(d, a, 12, -85, 0.4);
        Link(a, d, 12, -85, 0.4);

        // Settings: Alpha has everything, Bravo only tx_ref_rssi, Charlie only rx_adj_rssi.
        Nsd.Setup(x => x.Get("node-a")).Returns(new NodeSettings { Id = "node-a", Calibration = new CalibrationSettings { TxRefRssi = -59, RxAdjRssi = 2, Absorption = 2.7 } });
        Nsd.Setup(x => x.Get("node-b")).Returns(new NodeSettings { Id = "node-b", Calibration = new CalibrationSettings { TxRefRssi = -61 } });
        Nsd.Setup(x => x.Get("node-c")).Returns(new NodeSettings { Id = "node-c", Calibration = new CalibrationSettings { RxAdjRssi = -3 } });

        // Anchored device heard by Alpha and Bravo.
        var anchored = new Device("anchor-1", null, TimeSpan.FromSeconds(30)) { Name = "Anchor One" };
        anchored.SetAnchor(new DeviceAnchor(new Point3D(2, 0, 1), null, null));
        anchored.Nodes["node-a"] = new DeviceToNode(anchored, a) { Distance = 2.25, Rssi = -60, DistVar = 0.75, LastHit = DateTime.UtcNow };
        anchored.Nodes["node-b"] = new DeviceToNode(anchored, b) { Distance = 1.5, Rssi = -58, LastHit = DateTime.UtcNow };
        // Delta hears it too; with no fixed distance to the anchor it gets no cell and no R/RMSE pair.
        anchored.Nodes["node-d"] = new DeviceToNode(anchored, d) { Distance = 10, Rssi = -90, LastHit = DateTime.UtcNow };
        State.Devices[anchored.Id] = anchored;

        // An anchored device with no current readings stays out; a plain device never appears.
        var quiet = new Device("anchor-2", null, TimeSpan.FromSeconds(30)) { Name = "Anchor Two" };
        quiet.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), null, null));
        quiet.Nodes["node-a"] = new DeviceToNode(quiet, a) { Distance = 1, Rssi = -50, LastHit = DateTime.UtcNow.AddHours(-1) };
        State.Devices[quiet.Id] = quiet;

        var roaming = new Device("phone-1", null, TimeSpan.FromSeconds(30)) { Name = "Phone" };
        roaming.Nodes["node-a"] = new DeviceToNode(roaming, a) { Distance = 1, Rssi = -50, LastHit = DateTime.UtcNow };
        State.Devices[roaming.Id] = roaming;
    }

    public StateController CreateController() => new(
        Mock.Of<ILogger<StateController>>(),
        State,
        ConfigLoader,
        Nsd.Object,
        DeviceSettings,
        NodeTelemetry,
        new NodeStateMapper(NodeTelemetry, Mock.Of<IFirmwareTypeStore>()),
        new Mock<GlobalEventDispatcher>().Object,
        new CalibrationService(State, Nsd.Object),
        new DeviceEnricher(DeviceSettings));

    public McpResources CreateMcp() => new(
        State,
        ConfigLoader,
        Nsd.Object,
        NodeTelemetry,
        new DeviceEnricher(DeviceSettings),
        new TelemetryService(null!),
        null!,
        null!,
        new NodeStateMapper(NodeTelemetry, Mock.Of<IFirmwareTypeStore>()),
        new CalibrationService(State, Nsd.Object));

    /// <summary>
    /// Replaces the anchored device with one named like a node ("Bravo"), so its row and cell collide with
    /// Bravo's node row. The only current anchor, so the pinned order does not depend on dictionary order.
    /// </summary>
    public void UseAnchorNamedLikeNode()
    {
        State.Devices.TryRemove("anchor-1", out _);
        var a = State.Nodes["node-a"];
        var clash = new Device("anchor-3", null, TimeSpan.FromSeconds(30)) { Name = "Bravo" };
        clash.SetAnchor(new DeviceAnchor(new Point3D(6, 0, 1), null, null));
        clash.Nodes["node-a"] = new DeviceToNode(clash, a) { Distance = 5.5, Rssi = -75, LastHit = DateTime.UtcNow };
        State.Devices[clash.Id] = clash;
    }

    public async ValueTask DisposeAsync()
    {
        await ConfigLoader.StopAsync(CancellationToken.None);
        ConfigLoader.Dispose();
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
