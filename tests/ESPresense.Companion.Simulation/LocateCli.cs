using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Simulation.Tests;

namespace ESPresense.Simulation;

/// <summary>
/// stdin/stdout JSON wrapper around the production ILocate implementations.
/// See README.md for the request/response shape.
/// </summary>
public static class LocateCli
{
    public static int Run(TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string raw;
        try
        {
            raw = stdin.ReadToEnd();
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Failed to read stdin: {ex.Message}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            stderr.WriteLine("Empty stdin. Pipe a JSON request describing floor_bounds, locator, stations and distances.");
            return 2;
        }

        LocateRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<LocateRequest>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"Invalid request JSON: {ex.Message}");
            return 2;
        }

        if (req == null)
        {
            stderr.WriteLine("Request deserialized to null.");
            return 2;
        }

        if (!TryValidate(req, out var validationError))
        {
            stderr.WriteLine(validationError);
            return 2;
        }

        try
        {
            var response = Solve(req!);
            JsonSerializer.Serialize(stdout, response, JsonOpts);
            stdout.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Locate failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static bool TryValidate(LocateRequest req, out string error)
    {
        if (req.FloorBounds == null || req.FloorBounds.Length != 2)
        {
            error = "floor_bounds must be a 2-element array of [x,y,z] points.";
            return false;
        }
        foreach (var pt in req.FloorBounds)
        {
            if (pt == null || pt.Length < 3)
            {
                error = "floor_bounds points must each have at least 3 components (x,y,z).";
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(req.Locator))
        {
            error = "locator is required (NelderMead | GaussNewton | BFGS | MLE).";
            return false;
        }
        if (req.Stations == null || req.Stations.Count == 0)
        {
            error = "stations is required and must be non-empty.";
            return false;
        }
        if (req.Distances == null || req.Distances.Count == 0)
        {
            error = "distances is required and must be non-empty.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static LocateResponse Solve(LocateRequest req)
    {
        // Build a minimal in-memory Floor/State/Device wired up the same way
        // the existing MultilaterationSimulator does it.
        var configFloor = new ConfigFloor
        {
            Name = "harness",
            Bounds = new[]
            {
                new[] { req.FloorBounds![0][0], req.FloorBounds[0][1], req.FloorBounds[0][2] },
                new[] { req.FloorBounds![1][0], req.FloorBounds[1][1], req.FloorBounds[1][2] }
            }
        };
        var config = new Config { Floors = new[] { configFloor } };

        var floor = new Floor();
        floor.Update(config, configFloor);

        var configLoader = new MockConfigLoader(config);
        var nodeTelemetryStore = new MockNodeTelemetryStore();
        var state = new State(configLoader, nodeTelemetryStore);

        var device = new Device("harness-device", "harness", TimeSpan.FromSeconds(30));
        state.Devices[device.Id] = device;

        var nodesById = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in req.Stations!)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                throw new InvalidOperationException("station.id is required for each station.");

            var node = new Node(s.Id, NodeSourceType.Config);
            var configNode = new ConfigNode
            {
                Name = s.Id,
                Id = s.Id,
                Point = new[] { s.X, s.Y, s.Z }
            };
            node.Update(config, configNode, new[] { floor });
            nodesById[s.Id] = node;
            state.Nodes[s.Id] = node;
        }

        var now = DateTime.UtcNow;
        foreach (var d in req.Distances!)
        {
            if (string.IsNullOrWhiteSpace(d.StationId))
                throw new InvalidOperationException("distance.station_id is required.");
            if (!nodesById.TryGetValue(d.StationId, out var node))
                throw new InvalidOperationException($"distance.station_id '{d.StationId}' has no matching station entry.");

            device.Nodes[node.Id] = new DeviceToNode(device, node)
            {
                Distance = Math.Max(0.1, d.DistanceM),
                LastHit = now
            };
        }

        ILocate locator = req.Locator!.Trim().ToLowerInvariant() switch
        {
            "neldermead" or "nelder-mead" or "nm" => new NelderMeadMultilateralizer(device, floor, state),
            "gaussnewton" or "gauss-newton" or "gn" => new GaussNewtonMultilateralizer(device, floor, state),
            "bfgs" => new BfgsMultilateralizer(device, floor, state),
            "mle" => new MLEMultilateralizer(device, floor, state),
            _ => throw new InvalidOperationException(
                $"unknown locator '{req.Locator}'. Supported: NelderMead, GaussNewton, BFGS, MLE.")
        };

        var scenario = new Scenario(config, locator, "harness")
        {
            Confidence = 0
        };

        bool moved = locator.Locate(scenario);

        var loc = scenario.Location;
        return new LocateResponse
        {
            X = loc.X,
            Y = loc.Y,
            Z = loc.Z,
            Confidence = scenario.Confidence,
            Fixes = scenario.Fixes,
            Error = scenario.Error,
            Iterations = scenario.Iterations,
            ReasonForExit = scenario.ReasonForExit.ToString(),
            Moved = moved
        };
    }

    private sealed class LocateRequest
    {
        [JsonPropertyName("floor_bounds")]
        public double[][]? FloorBounds { get; set; }

        [JsonPropertyName("locator")]
        public string? Locator { get; set; }

        [JsonPropertyName("stations")]
        public List<Station>? Stations { get; set; }

        [JsonPropertyName("distances")]
        public List<DistanceReading>? Distances { get; set; }
    }

    private sealed class Station
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("z")]
        public double Z { get; set; }
    }

    private sealed class DistanceReading
    {
        [JsonPropertyName("station_id")]
        public string? StationId { get; set; }

        [JsonPropertyName("distance_m")]
        public double DistanceM { get; set; }
    }

    private sealed class LocateResponse
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("z")]
        public double Z { get; set; }

        [JsonPropertyName("confidence")]
        public int? Confidence { get; set; }

        [JsonPropertyName("fixes")]
        public int? Fixes { get; set; }

        [JsonPropertyName("error")]
        public double? Error { get; set; }

        [JsonPropertyName("iterations")]
        public int? Iterations { get; set; }

        [JsonPropertyName("reason_for_exit")]
        public string? ReasonForExit { get; set; }

        [JsonPropertyName("moved")]
        public bool Moved { get; set; }
    }
}
