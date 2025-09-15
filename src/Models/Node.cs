using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;
using SQLite;
using ESPresense.Extensions;

namespace ESPresense.Models;

public class Node(string id, NodeSourceType sourceType)
{
    [JsonIgnore]
    public Config? Config { get; private set; }

    [PrimaryKey]
    public string Id { get; } = id;

    public NodeSourceType SourceType { get; } = sourceType;

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


    public void Update(Config c, ConfigNode cn, IEnumerable<Floor> floors)
    {
        Config = c;
        Name = cn.Name;
        Floors = floors.ToArray();
        double[]? point = cn.Point?.EnsureLength(3);
        Location = new Point3D(point?[0] ?? 0, point?[1] ?? 0, point?[2] ?? 0);
        Stationary = cn.Stationary;
    }

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