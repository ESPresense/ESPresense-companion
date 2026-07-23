using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;
using Serilog;
using SQLite;
using ESPresense.Extensions;

namespace ESPresense.Models;

public class Node(string id, NodeSourceType sourceType)
{
    [JsonIgnore]
    public Config? Config { get; private set; }

    [PrimaryKey]
    public string Id { get; } = id;

    public NodeSourceType SourceType { get; private set; } = sourceType;

    public string? Name { get; private set; }

    public double? X { get; private set; }
    public double? Y { get; private set; }
    public double? Z { get; private set; }
    public bool HasLocation => X.HasValue && Y.HasValue && Z.HasValue;
    public bool Stationary { get; private set; }

    [JsonConverter(typeof(NodeToNodeConverter))]
    public ConcurrentDictionary<string, NodeToNode> Nodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

    [Ignore][JsonConverter(typeof(Point3DConverter))]
    public Point3D Location
    {
        get => new(X ?? 0, Y ?? 0, Z ?? 0);
        private set
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
        }
    }


    /// <summary>
    /// Update this Node's metadata and state from the provided configuration data.
    /// </summary>
    /// <remarks>
    /// Sets the Node's Config, Name, Floors, Location (X/Y/Z), Stationary flag, and marks SourceType as <see cref="NodeSourceType.Config"/>.
    /// </remarks>
    /// <param name="c">The root configuration object containing broader context for the node.</param>
    /// <param name="cn">The node-specific configuration used to populate name, point, and stationary state.</param>
    /// <param name="floors">Collection of floors the node belongs to; stored as an array on the Node.</param>
    public void Update(Config c, ConfigNode cn, IEnumerable<Floor> floors)
    {
        Config = c;
        Name = cn.Name;
        Floors = floors.ToArray();
        double[]? point = cn.Point?.EnsureLength(3);
        Location = new Point3D(point?[0] ?? 0, point?[1] ?? 0, point?[2] ?? 0);
        ZOutOfBounds = point != null && Floors.Any(f => f.Bounds is { Length: >= 2 }) && !Floors.Any(f => f.Contained(Location.Z));
        if (ZOutOfBounds)
            Log.Warning("Node {Node} z={Z} is outside the z-range of its floor(s) {Floors}. Coordinates are absolute: z must include the floor's base elevation (a node 1.7m up on a floor whose bounds start at z=3 has z=4.7). This node will be excluded from locating on those floors.",
                Name ?? Id, Location.Z,
                string.Join(", ", Floors.Where(f => f.Bounds is { Length: >= 2 }).Select(f => $"{f.Id} [{f.Bounds![0].Z}..{f.Bounds[1].Z}]")));
        Stationary = cn.Stationary;
        SourceType = NodeSourceType.Config;
    }

    /// <summary>True when a configured point's z falls outside the z-range of every floor this node is assigned to (a common sign of floor-relative instead of absolute coordinates).</summary>
    [Ignore]
    public bool ZOutOfBounds { get; private set; }

    public Floor[]? Floors { get; private set; }

    public ConcurrentDictionary<string, RxNode> RxNodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public IEnumerable<KeyValuePair<string, string>> GetDetails()
    {
        yield return new("Id", Id);
        yield return new("Name", Name ?? "");
        yield return new("X", X?.ToString() ?? "");
        yield return new("Y", Y?.ToString() ?? "");
        yield return new("Z", Z?.ToString() ?? "");
        yield return new("Stationary", Stationary.ToString());
        yield return new("Floors", Floors?.Length.ToString() ?? "");
        yield return new("Nodes", Nodes.Count.ToString());
        yield return new("RxNodes", RxNodes.Count.ToString());
    }
}