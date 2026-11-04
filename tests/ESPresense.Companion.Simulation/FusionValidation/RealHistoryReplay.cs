using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// Drives the real <see cref="CsiFusionService"/>/<see cref="CsiMotionTracker"/> through an exported
/// Home Assistant history file (<see cref="HaHistoryClient.LoadFromFile"/> format) instead of a
/// synthetic timeline, for ESPA-466/PR #1838's real-data validation pass.
///
/// Scope and honest limits (see the PR description for the full writeup):
///  - The export has no per-room BLE scenario probabilities (only 14 sparse rows, not a real time
///    series), so there is nothing to feed <see cref="CsiFusionService.DecideDeviceRoom"/>'s tie-break
///    path (it needs >=2 concurrent room candidates). Each tracked device gets a single scenario set
///    to its device_tracker-reported room, so this replay exercises ingestion, per-node baseline,
///    motion, damping, attribution and unknown-occupant -- not the tie-break margin itself.
///  - FlapResolutionRate's ground truth is the same device_tracker stream being fused (see
///    ValidationMetrics.FlapResolutionRate's doc comment) -- for live data that is not an independent
///    check of correctness, only of how often the fused output still agrees with the raw, flapping
///    signal during a labeled episode. Read a low rate during a real flap as consistent with damping
///    doing its job, not as an accuracy failure.
///  - Never load or print row-level content from the export outside this process; only aggregate
///    ValidationReport numbers are meant to leave this tool (e.g. piped into a committed report file).
/// </summary>
public static class RealHistoryReplay
{
    private static readonly string[] RoomIds = { "family", "laundry", "office", "t_embed" };

    private static readonly Dictionary<string, string> CsiEntityToNode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sensor.family_espresense_family_csi_variance"] = "family",
        ["sensor.laundry_espresense_laundry_csi_variance"] = "laundry",
        ["sensor.office_espresense_office_csi_variance"] = "office",
        ["sensor.t_embed_espresense_t_embed_csi_variance"] = "t_embed",
    };

    private static readonly string[] TrackerEntities =
    {
        "device_tracker.phone_darrell_15_pro",
        "device_tracker.phone_piper",
        "device_tracker.phone_old_iphone",
    };

    public static int Run(string[] args)
    {
        Opts opts;
        Dictionary<string, List<HaStateChange>> history;
        try
        {
            opts = ParseOpts(args);
            history = HaHistoryClient.LoadFromFile(opts.HaExportPath);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: dotnet run -- fusion-replay-real --ha-export FILE [--start ISO] [--end ISO] [--output FILE] [--markdown FILE] [--exclude-entity ID]");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"fusion-replay-real failed: {ex.Message}");
            return 1;
        }

        var (state, tracker, fusion) = BuildHarness();

        var csiQueues = CsiEntityToNode.Keys
            .ToDictionary(e => e, e => new Queue<(DateTimeOffset At, double Value)>(
                WindowWithSeed(history.GetValueOrDefault(e) ?? new List<HaStateChange>(), opts.Start, opts.End)
                    .Select(c => (c.LastChanged, ParseOrNaN(c.State)))
                    .Where(c => double.IsFinite(c.Item2))),
                StringComparer.OrdinalIgnoreCase);

        var deviceIds = TrackerEntities.ToDictionary(e => e, e => e["device_tracker.".Length..]);
        foreach (var deviceId in deviceIds.Values)
            state.Devices[deviceId] = new Device(deviceId, null, TimeSpan.FromSeconds(30)) { Track = true };

        var trackerQueues = TrackerEntities
            .ToDictionary(e => e, e => new Queue<(DateTimeOffset At, string State)>(
                WindowWithSeed(history.GetValueOrDefault(e) ?? new List<HaStateChange>(), opts.Start, opts.End)
                    .Select(c => (c.LastChanged, c.State))),
                StringComparer.OrdinalIgnoreCase);

        var roomIdSet = new HashSet<string>(RoomIds, StringComparer.OrdinalIgnoreCase);
        var events = new List<FusionRoomEvent>();

        for (var at = opts.Start; at <= opts.End; at = at.AddSeconds(5))
        {
            foreach (var (entity, nodeId) in CsiEntityToNode)
            {
                var q = csiQueues[entity];
                while (q.Count > 0 && q.Peek().At <= at)
                {
                    var (ts, value) = q.Dequeue();
                    tracker.Observe(nodeId, value, ts.UtcDateTime);
                }
            }

            foreach (var entity in TrackerEntities)
            {
                var q = trackerQueues[entity];
                var device = state.Devices[deviceIds[entity]];
                while (q.Count > 0 && q.Peek().At <= at)
                {
                    var (ts, rawState) = q.Dequeue();
                    var roomId = roomIdSet.Contains(rawState) ? rawState : null;
                    device.BestScenario = roomId is null
                        ? null
                        : new Scenario(null, null!, "real") { Room = state.Floors["house"].Rooms[roomId], Probability = 0.9, LastHit = ts.UtcDateTime };
                }
            }

            foreach (var occupancy in fusion.Tick(at.UtcDateTime))
                events.Add(FusionRoomEvent.FromRoomOccupancy(at, occupancy));
        }

        string RoomFromOccupancyEntity(string entityId) =>
            entityId.Equals("binary_sensor.family_presence_occupancy", StringComparison.OrdinalIgnoreCase)
                ? "family"
                : FusionValidationCli.RoomFromOccupancyEntity(entityId);

        string? RoomFromTrackerState(string trackerState) => roomIdSet.Contains(trackerState) ? trackerState : null;

        // Restrict ground truth to the replay window: the export covers 2026-11-01 onward, but the
        // fused output only exists for [Start, End]. An occupancy/tracker interval entirely outside
        // that window can never have a matching fused event, so scoring against the full history would
        // dilute false-unknown/latency with trivially-unmatched samples rather than real misses.
        var windowedHistory = history.ToDictionary(
            kv => kv.Key,
            kv => WindowWithSeed(kv.Value, opts.Start, opts.End).ToList());

        // Score only the rooms that actually have a CSI node (plus the stuck family_occupancy DT
        // flagged): the export has occupancy ground truth for every room in the house, but Latency and
        // FalseUnknownRate against a room fusion can never report on (no CSI coverage there at all)
        // would just add always-null/always-empty noise, not a real miss.
        var excluded = new HashSet<string>(opts.ExcludedEntityIds, StringComparer.OrdinalIgnoreCase);
        foreach (var entityId in windowedHistory.Keys)
        {
            if (!entityId.StartsWith("binary_sensor.", StringComparison.OrdinalIgnoreCase) || !entityId.EndsWith("_occupancy", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!roomIdSet.Contains(RoomFromOccupancyEntity(entityId)))
                excluded.Add(entityId);
        }

        var groundTruth = GroundTruthBuilder.Build(windowedHistory, excluded, RoomFromOccupancyEntity, RoomFromTrackerState);
        var flapWindows = DetectFlapWindows(history, "device_tracker.phone_darrell_15_pro", opts.Start, opts.End);

        var report = ValidationMetrics.Compute(events, groundTruth, flapWindows, opts.LatencyWindow);
        var json = FusionValidationReportWriter.ToJson(report);
        var md = FusionValidationReportWriter.ToMarkdown(report);

        var windowNote = $"\n_Window: {opts.Start:O} to {opts.End:O} ({(opts.End - opts.Start).TotalHours:F1}h). " +
                          $"{flapWindows.Count} flap window(s) auto-detected from device_tracker.phone_darrell_15_pro. " +
                          "Real HA history -- see PR description for scope/limits (no scenario-probability data, " +
                          "so tie-break itself is not exercised; FlapResolutionRate's ground truth is the same " +
                          "device_tracker stream being fused, not an independent check)._\n";

        if (opts.JsonOutput != null) File.WriteAllText(opts.JsonOutput, json);
        else Console.Out.Write(json);

        if (opts.MarkdownOutput != null) File.WriteAllText(opts.MarkdownOutput, md + windowNote);
        else Console.Out.Write(md + windowNote);

        return 0;
    }

    /// <summary>Finds episodes where the tracked device bounces back to a room it had just left
    /// (A -> B -> A) within a minute, the shape of the documented BLE-flapping problem. Each maximal
    /// run of such bounces becomes one <see cref="FlapWindow"/>; this reads the device's own history,
    /// it does not fabricate one.</summary>
    private static List<FlapWindow> DetectFlapWindows(Dictionary<string, List<HaStateChange>> history, string entityId, DateTimeOffset start, DateTimeOffset end)
    {
        if (!history.TryGetValue(entityId, out var changes)) return new List<FlapWindow>();
        var inWindow = changes.Where(c => c.LastChanged >= start && c.LastChanged <= end).OrderBy(c => c.LastChanged).ToList();
        var deviceId = entityId["device_tracker.".Length..];

        var windows = new List<FlapWindow>();
        DateTimeOffset? windowStart = null;
        DateTimeOffset? windowEnd = null;

        for (var i = 2; i < inWindow.Count; i++)
        {
            var bounced = inWindow[i].State == inWindow[i - 2].State
                && inWindow[i].State != inWindow[i - 1].State
                && (inWindow[i].LastChanged - inWindow[i - 2].LastChanged) <= TimeSpan.FromMinutes(1);

            if (bounced)
            {
                windowStart ??= inWindow[i - 2].LastChanged;
                windowEnd = inWindow[i].LastChanged;
            }
            else if (windowStart is not null)
            {
                windows.Add(new FlapWindow(deviceId, windowStart.Value, windowEnd!.Value));
                windowStart = null;
            }
        }
        if (windowStart is not null) windows.Add(new FlapWindow(deviceId, windowStart.Value, windowEnd!.Value));
        return windows;
    }

    /// <summary>
    /// Home Assistant only writes a history row when a state changes, so an entity's state already in
    /// effect at <paramref name="start"/> is usually set by a row before it. A plain <c>&gt;= start</c>
    /// filter drops that row -- the replay then has no BLE room for a device that hasn't changed state
    /// since before the window, and GroundTruthBuilder has no interval for a room already occupied
    /// coming in, scoring every early Unknown event there as a false alarm. Seed the window with that
    /// last pre-window row, clamped to <paramref name="start"/> so the resulting interval stays inside it.
    /// </summary>
    private static IEnumerable<HaStateChange> WindowWithSeed(List<HaStateChange> changes, DateTimeOffset start, DateTimeOffset end)
    {
        var ordered = changes.OrderBy(c => c.LastChanged).ToList();
        var seed = ordered.LastOrDefault(c => c.LastChanged < start);
        if (seed is not null) yield return seed with { LastChanged = start };
        foreach (var c in ordered.Where(c => c.LastChanged >= start && c.LastChanged <= end))
            yield return c;
    }

    private static double ParseOrNaN(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private static (State State, CsiMotionTracker Tracker, CsiFusionService Service) BuildHarness()
    {
        var config = new Config();
        var floor = new Floor();
        floor.Update(config, new ConfigFloor
        {
            Id = "house",
            Name = "house",
            Bounds = new[] { new[] { 0.0, 0, 0 }, new[] { 40.0, 40, 3 } },
            Rooms = RoomIds.Select((id, i) => new ConfigRoom { Name = id, Points = Square(i * 4) }).ToArray()
        });

        var mqtt = new NullMqttCoordinator();
        var mockConfigLoader = new MockConfigLoaderForFusion(config);
        var nodeTelemetryStore = new MockNodeTelemetryStoreForFusion();
        var state = new State(mockConfigLoader, nodeTelemetryStore);
        state.Floors["house"] = floor;

        foreach (var (id, i) in RoomIds.Select((id, i) => (id, i)))
        {
            var node = new Node(id, NodeSourceType.Config);
            node.Update(config, new ConfigNode { Id = id, Name = id, Point = new[] { i * 4.0 + 1, 1, 1 } }, new[] { floor });
            state.Nodes[id] = node;
        }

        var tracker = new CsiMotionTracker(mqtt);
        var fusionService = new CsiFusionService(state, tracker, mqtt, new GlobalEventDispatcher(), NullLogger<CsiFusionService>.Instance);
        return (state, tracker, fusionService);
    }

    private static double[][] Square(double x0) => new[] { new[] { x0, 0 }, new[] { x0 + 3, 0 }, new[] { x0 + 3, 3 }, new[] { x0, 3 } };

    private sealed class Opts
    {
        public string HaExportPath { get; set; } = "";
        public DateTimeOffset Start { get; set; } = DateTimeOffset.Parse("2026-11-04T08:00:00Z", CultureInfo.InvariantCulture);
        public DateTimeOffset End { get; set; } = DateTimeOffset.Parse("2026-11-04T19:11:00Z", CultureInfo.InvariantCulture);
        public TimeSpan LatencyWindow { get; set; } = TimeSpan.FromMinutes(10);
        public HashSet<string> ExcludedEntityIds { get; } = new() { "binary_sensor.family_occupancy" };
        public string? JsonOutput { get; set; }
        public string? MarkdownOutput { get; set; }
    }

    private static Opts ParseOpts(string[] args)
    {
        var o = new Opts();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {args[i - 1]}");
            switch (args[i])
            {
                case "--ha-export": o.HaExportPath = Next(); break;
                case "--start": o.Start = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--end": o.End = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--output": o.JsonOutput = Next(); break;
                case "--markdown": o.MarkdownOutput = Next(); break;
                case "--exclude-entity": o.ExcludedEntityIds.Add(Next()); break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if (string.IsNullOrEmpty(o.HaExportPath)) throw new ArgumentException("--ha-export is required");
        return o;
    }
}
