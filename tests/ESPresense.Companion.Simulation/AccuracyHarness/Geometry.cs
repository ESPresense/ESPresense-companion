using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Simulation.AccuracyHarness;

public sealed class HarnessScenario
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("metadata")] public Dictionary<string, JsonElement>? Metadata { get; init; }
    [JsonPropertyName("bounds")] public HarnessBounds Bounds { get; init; } = new();
    [JsonPropertyName("rooms")] public HarnessRoom[] Rooms { get; init; } = Array.Empty<HarnessRoom>();
    [JsonPropertyName("stations")] public HarnessStation[] Stations { get; init; } = Array.Empty<HarnessStation>();
    [JsonPropertyName("tracks")] public HarnessTrack[] Tracks { get; init; } = Array.Empty<HarnessTrack>();

    /// <summary>
    /// First-match-wins lookup of the room containing (x,y). Room bounds are
    /// inclusive on both sides — a point on a shared boundary belongs to
    /// the room declared first in the scenario file. This is deterministic
    /// but documented so a future contributor does not "fix" it.
    /// </summary>
    public string? RoomFor(double x, double y)
    {
        foreach (var r in Rooms)
            if (r.Contains(x, y))
                return r.Name;
        return null;
    }

    public static HarnessScenario Load(string path)
    {
        var json = File.ReadAllText(path);
        var s = JsonSerializer.Deserialize<HarnessScenario>(json, Json.Options)
                ?? throw new InvalidOperationException($"Failed to parse scenario {path}");
        return s;
    }
}

public sealed class HarnessBounds
{
    [JsonPropertyName("x_min")] public double XMin { get; init; }
    [JsonPropertyName("y_min")] public double YMin { get; init; }
    [JsonPropertyName("x_max")] public double XMax { get; init; }
    [JsonPropertyName("y_max")] public double YMax { get; init; }
}

public sealed class HarnessRoom
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("x_min")] public double XMin { get; init; }
    [JsonPropertyName("y_min")] public double YMin { get; init; }
    [JsonPropertyName("x_max")] public double XMax { get; init; }
    [JsonPropertyName("y_max")] public double YMax { get; init; }

    public bool Contains(double x, double y) =>
        x >= XMin && x <= XMax && y >= YMin && y <= YMax;
}

public sealed class HarnessStation
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("x")] public double X { get; init; }
    [JsonPropertyName("y")] public double Y { get; init; }
    [JsonPropertyName("z")] public double Z { get; init; }
    [JsonPropertyName("cal_rssi")] public double CalRssi { get; init; } = -59.0;
    [JsonPropertyName("absorption")] public double Absorption { get; init; } = 3.0;
}

public sealed class HarnessTrack
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("samples_per_segment")] public int SamplesPerSegment { get; init; } = 10;
    [JsonPropertyName("points")] public double[][] Points { get; init; } = Array.Empty<double[]>();

    /// <summary>
    /// Linearly interpolate waypoints into (x,y) samples. Inclusive on the
    /// final waypoint so a track of length N yields (N-1) * samplesPerSegment + 1
    /// samples, matching the Python harness shape.
    /// </summary>
    public List<(double X, double Y)> Expand()
    {
        var pts = new List<(double, double)>();
        if (Points.Length == 0) return pts;
        for (int i = 0; i < Points.Length - 1; i++)
        {
            double x0 = Points[i][0], y0 = Points[i][1];
            double x1 = Points[i + 1][0], y1 = Points[i + 1][1];
            for (int s = 0; s < SamplesPerSegment; s++)
            {
                double t = (double)s / SamplesPerSegment;
                pts.Add((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
            }
        }
        var last = Points[^1];
        pts.Add((last[0], last[1]));
        return pts;
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
