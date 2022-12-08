using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;
using SQLite;

namespace ESPresense.Models;

public class Node
{
    public Node()
    {
    }

    public Node(Config config, ConfigNode node)
    {
        Name = node.Name;
        Id = node.GetId();
        Location = new Point3D(node?.Point?[0] ?? 0, node?.Point?[1] ?? 0, node?.Point?[2] ?? 0);
        Config = config;
    }

    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    [PrimaryKey]
    public string? Id { get; set; }
    public string? Name { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }

    [Ignore][JsonConverter(typeof(Point3DConverter))]
    public Point3D Location
    {
        get => new Point3D(X, Y, Z);
        set
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
        }
    }

    public Config? Config { get; }
}