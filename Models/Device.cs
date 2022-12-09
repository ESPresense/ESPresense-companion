﻿using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ESPresense.Converters;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Models;

public class Device
{
    public override string ToString()
    {
        return $"{nameof(Id)}: {Id}";
    }

    public string? Id { get; set; }
    public string? Name { get; set; }
    [JsonConverter(typeof(Point3DConverter))]
    public Point3D Location { get; set; }
    [JsonConverter(typeof(Point3DConverter))]
    public Point3D ReportedLocation { get; set; }
    [JsonConverter(typeof(NodeDistanceConverter))]
    public ConcurrentDictionary<string, DeviceNode> Nodes { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
    public double Scale { get; set; } = 1;
}