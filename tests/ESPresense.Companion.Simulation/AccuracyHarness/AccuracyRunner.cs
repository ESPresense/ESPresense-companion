using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ESPresense.Locators;
using ESPresense.Models;
using ESPresense.Simulation.Tests;

namespace ESPresense.Simulation.AccuracyHarness;

public enum LocatorKind
{
    NelderMead,
    GaussNewton,
    Bfgs,
    Mle
}

public sealed class TrackResult
{
    public string Track { get; init; } = "";
    public int Samples { get; set; }
    public double MedianErrorM { get; set; }
    public double P95ErrorM { get; set; }
    public double MeanErrorM { get; set; }
    public double MaxErrorM { get; set; }
    public double RoomMisidRate { get; set; }
    public Dictionary<string, OutageStat> SingleStationOutage { get; set; } = new();
}

public sealed class OutageStat
{
    public int Samples { get; set; }
    public double MedianErrorM { get; set; }
    public double P95ErrorM { get; set; }
}

public sealed class ScenarioReport
{
    public string Scenario { get; init; } = "";
    public string Locator { get; init; } = "";
    public long BaseSeed { get; init; }
    public NoiseProfile Noise { get; init; } = new();
    public List<TrackResult> Tracks { get; init; } = new();
}

/// <summary>
/// Drives a real <see cref="ILocate"/> through the simulated scenarios and
/// computes per-track accuracy metrics. This is what makes the output a
/// number on the real product, not on a Python lower-bound stub.
/// </summary>
public static class AccuracyRunner
{
    public static ScenarioReport Run(
        HarnessScenario scenario,
        LocatorKind locatorKind,
        long baseSeed,
        NoiseProfile noise)
    {
        var report = new ScenarioReport
        {
            Scenario = scenario.Name,
            Locator = locatorKind.ToString(),
            BaseSeed = baseSeed,
            Noise = noise
        };

        foreach (var track in scenario.Tracks)
        {
            report.Tracks.Add(ScoreTrack(scenario, track, locatorKind, baseSeed, noise));
        }
        return report;
    }

    private static TrackResult ScoreTrack(
        HarnessScenario scenario,
        HarnessTrack track,
        LocatorKind locatorKind,
        long baseSeed,
        NoiseProfile noise)
    {
        var samples = track.Expand();
        var errors = new List<double>();
        int misid = 0;
        int validRoomSamples = 0;

        // Per-station outage tracking: drop each station once per sample,
        // re-solve, collect error distribution.
        var outageErrors = scenario.Stations.ToDictionary(s => s.Id, _ => new List<double>());

        // Build the simulation context ONCE per track and reuse it for every
        // solve below (full-fix and each outage drop). The *Multilateralizer
        // locators are stateless across Locate() calls: they read Device.Nodes
        // (which SolveOnce clears and repopulates each call) and write a fresh
        // Scenario, so reusing the context is byte-identical to rebuilding it,
        // just without the per-solve State/Device/Node/Floor allocations. The
        // Device Kalman filter lives in MultiScenarioLocator, not in these
        // locators, so there is no cross-call continuity to preserve here.
        var ctx = BuildContext(scenario, locatorKind);

        var rng = new Random(unchecked((int)SeedFor(baseSeed, scenario.Name, track.Name)));

        foreach (var (tx, ty) in samples)
        {
            // Synthesize a noisy distance per station via the firmware formula.
            var measured = scenario.Stations
                .Select(st =>
                {
                    double dx = tx - st.X, dy = ty - st.Y;
                    double trueD = Math.Sqrt(dx * dx + dy * dy);
                    double trueRssi = FirmwareDistanceModel.DistanceToRssi(st.CalRssi, trueD, st.Absorption);
                    double noisyRssi = noise.Sample(rng, trueRssi);
                    double measuredD = FirmwareDistanceModel.RssiToDistance(st.CalRssi, noisyRssi, st.Absorption);
                    return (Station: st, DistanceM: Math.Max(0.1, measuredD));
                })
                .ToArray();

            // Full-fix solve
            var solve = SolveOnce(ctx, measured);
            if (solve.HasValue)
            {
                double ex = solve.Value.X - tx;
                double ey = solve.Value.Y - ty;
                errors.Add(Math.Sqrt(ex * ex + ey * ey));

                var trueRoom = scenario.RoomFor(tx, ty);
                if (trueRoom != null)
                {
                    validRoomSamples++;
                    var estRoom = scenario.RoomFor(solve.Value.X, solve.Value.Y);
                    if (trueRoom != estRoom) misid++;
                }
            }

            // Single-station outage sweep
            for (int dropIdx = 0; dropIdx < measured.Length; dropIdx++)
            {
                if (measured.Length - 1 < 3) break;
                var subset = measured.Where((_, i) => i != dropIdx).ToArray();
                var droppedId = measured[dropIdx].Station.Id;
                var outSolve = SolveOnce(ctx, subset);
                if (outSolve.HasValue)
                {
                    double ex = outSolve.Value.X - tx;
                    double ey = outSolve.Value.Y - ty;
                    outageErrors[droppedId].Add(Math.Sqrt(ex * ex + ey * ey));
                }
            }
        }

        return new TrackResult
        {
            Track = track.Name,
            Samples = errors.Count,
            MedianErrorM = Percentile(errors, 0.5),
            P95ErrorM = Percentile(errors, 0.95),
            MeanErrorM = errors.Count > 0 ? errors.Average() : double.NaN,
            MaxErrorM = errors.Count > 0 ? errors.Max() : double.NaN,
            RoomMisidRate = validRoomSamples > 0 ? (double)misid / validRoomSamples : 0.0,
            SingleStationOutage = outageErrors.ToDictionary(
                kv => kv.Key,
                kv => new OutageStat
                {
                    Samples = kv.Value.Count,
                    MedianErrorM = Percentile(kv.Value, 0.5),
                    P95ErrorM = Percentile(kv.Value, 0.95)
                })
        };
    }

    private static (double X, double Y)? SolveOnce(SimulationContext ctx, (HarnessStation Station, double DistanceM)[] measured)
    {
        ctx.Device.Nodes.Clear();

        var now = DateTime.UtcNow;
        foreach (var (st, distance) in measured)
        {
            if (!ctx.Nodes.TryGetValue(st.Id, out var node)) continue;
            ctx.Device.Nodes[node.Id] = new DeviceToNode(ctx.Device, node)
            {
                Distance = distance,
                LastHit = now
            };
        }

        var scenario = new Scenario(ctx.Config, ctx.Locator, "accuracy-harness") { Confidence = 0 };
        bool moved = ctx.Locator.Locate(scenario);
        if (!moved) return null;

        var loc = scenario.Location;
        if (double.IsNaN(loc.X) || double.IsNaN(loc.Y) || double.IsInfinity(loc.X) || double.IsInfinity(loc.Y))
            return null;
        return (loc.X, loc.Y);
    }

    private sealed class SimulationContext
    {
        public Config Config { get; init; } = null!;
        public Floor Floor { get; init; } = null!;
        public State State { get; init; } = null!;
        public Device Device { get; init; } = null!;
        public Dictionary<string, Node> Nodes { get; init; } = new();
        public ILocate Locator { get; init; } = null!;
    }

    private static SimulationContext BuildContext(HarnessScenario scenario, LocatorKind kind)
    {
        var configFloor = new ConfigFloor
        {
            Name = scenario.Name,
            // Companion uses [x,y,z] bounds; the harness scenarios are 2D so we
            // inflate z to a fixed 3m ceiling consistent with the station
            // mounting in the baseline scenario.
            Bounds = new[]
            {
                new[] { scenario.Bounds.XMin, scenario.Bounds.YMin, 0.0 },
                new[] { scenario.Bounds.XMax, scenario.Bounds.YMax, 3.0 }
            }
        };
        var config = new Config { Floors = new[] { configFloor } };
        var floor = new Floor();
        floor.Update(config, configFloor);

        var configLoader = new MockConfigLoader(config);
        var nodeTelemetryStore = new MockNodeTelemetryStore();
        var state = new State(configLoader, nodeTelemetryStore);

        var nodes = new Dictionary<string, Node>();
        foreach (var st in scenario.Stations)
        {
            var node = new Node(st.Id, NodeSourceType.Config);
            var cn = new ConfigNode { Name = st.Id, Id = st.Id, Point = new[] { st.X, st.Y, st.Z } };
            node.Update(config, cn, new[] { floor });
            nodes[st.Id] = node;
            state.Nodes[st.Id] = node;
        }

        var device = new Device("accuracy-harness-device", "accuracy-harness", TimeSpan.FromSeconds(30));
        state.Devices[device.Id] = device;

        ILocate locator = kind switch
        {
            LocatorKind.NelderMead => new NelderMeadMultilateralizer(device, floor, state),
            LocatorKind.GaussNewton => new GaussNewtonMultilateralizer(device, floor, state),
            LocatorKind.Bfgs => new BfgsMultilateralizer(device, floor, state),
            LocatorKind.Mle => new MLEMultilateralizer(device, floor, state),
            _ => throw new InvalidOperationException($"Unsupported locator {kind}")
        };

        return new SimulationContext
        {
            Config = config,
            Floor = floor,
            State = state,
            Device = device,
            Nodes = nodes,
            Locator = locator
        };
    }

    private static long SeedFor(long baseSeed, string scenario, string track)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"{baseSeed}|{scenario}|{track}"));
        return BitConverter.ToInt64(bytes, 0);
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        if (values.Count == 1) return values[0];
        var sorted = values.OrderBy(v => v).ToList();
        double k = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(k);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (k - lo);
    }
}
