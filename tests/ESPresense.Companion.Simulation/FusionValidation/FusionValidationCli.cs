using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// Subcommand entry: <c>dotnet run -- fusion-validate</c>.
///
/// Scores a fused CSI+BLE output event log (ESPA-459..462) against Home Assistant
/// history, per ESPA-463. Both inputs are files today:
///   --ha-history FILE      cached HA /api/history/period JSON (see HaHistoryClient.SaveToFile)
///   --fusion-events FILE   fused room-event JSON (see FusionEvent.cs — PROVISIONAL schema;
///                          ESPA-462 had not landed when this CLI was written)
///
/// There is no live HA pull wired in here yet: this harness was built while parts 1-4
/// of the CSI fusion work (ESPA-459..462) were still `todo` and the underlying firmware
/// PR (ESPresense/ESPresense#2601) was still draft, so there was no real fused output to
/// point at. Once both exist, add a `--ha-url`/`--ha-token` live-pull path alongside the
/// file-based one (HaHistoryClient.FetchAsync already supports it) rather than ripping
/// out the file path — cached ground truth should stay replayable for CI per the
/// ESPA-463 acceptance criteria.
/// </summary>
public static class FusionValidationCli
{
    public static int Run(string[] args)
    {
        try
        {
            var opts = ParseOpts(args);
            var history = HaHistoryClient.LoadFromFile(opts.HaHistoryPath);
            var groundTruth = GroundTruthBuilder.Build(
                history,
                opts.ExcludedEntityIds,
                RoomFromOccupancyEntity,
                state => state);

            var events = FusionEventLog.LoadFromFile(opts.FusionEventsPath);
            var report = ValidationMetrics.Compute(events, groundTruth, opts.FlapWindows, opts.LatencyWindow);

            var json = FusionValidationReportWriter.ToJson(report);
            var md = FusionValidationReportWriter.ToMarkdown(report);

            if (opts.JsonOutput != null)
            {
                File.WriteAllText(opts.JsonOutput, json);
                Console.Error.WriteLine($"Wrote {opts.JsonOutput}");
            }
            else
            {
                Console.Out.Write(json);
            }

            if (opts.MarkdownOutput != null)
            {
                File.WriteAllText(opts.MarkdownOutput, md);
                Console.Error.WriteLine($"Wrote {opts.MarkdownOutput}");
            }
            else if (opts.JsonOutput != null)
            {
                Console.Out.Write(md);
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
    }

    internal static string RoomFromOccupancyEntity(string entityId)
    {
        const string prefix = "binary_sensor.";
        const string suffix = "_occupancy";
        var name = entityId;
        if (name.StartsWith(prefix, StringComparison.Ordinal)) name = name[prefix.Length..];
        if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length];
        return name;
    }

    private sealed class Opts
    {
        public string HaHistoryPath { get; set; } = "";
        public string FusionEventsPath { get; set; } = "";
        public HashSet<string> ExcludedEntityIds { get; } = new() { "binary_sensor.family_occupancy" };
        public List<FlapWindow> FlapWindows { get; } = new();
        public TimeSpan LatencyWindow { get; set; } = TimeSpan.FromMinutes(10);
        public string? JsonOutput { get; set; }
        public string? MarkdownOutput { get; set; }
    }

    private static Opts ParseOpts(string[] args)
    {
        var o = new Opts();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--ha-history": o.HaHistoryPath = Next(); break;
                case "--fusion-events": o.FusionEventsPath = Next(); break;
                case "--exclude-entity": o.ExcludedEntityIds.Add(Next()); break;
                case "--flap":
                    var parts = Next().Split(',');
                    if (parts.Length != 3) throw new ArgumentException("--flap expects deviceId,startIso,endIso");
                    o.FlapWindows.Add(new FlapWindow(parts[0], DateTimeOffset.Parse(parts[1], CultureInfo.InvariantCulture), DateTimeOffset.Parse(parts[2], CultureInfo.InvariantCulture)));
                    break;
                case "--latency-window-minutes": o.LatencyWindow = TimeSpan.FromMinutes(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--output": o.JsonOutput = Next(); break;
                case "--markdown": o.MarkdownOutput = Next(); break;
                default: throw new ArgumentException($"unknown option '{a}'");
            }
        }
        if (string.IsNullOrEmpty(o.HaHistoryPath)) throw new ArgumentException("--ha-history is required");
        if (string.IsNullOrEmpty(o.FusionEventsPath)) throw new ArgumentException("--fusion-events is required");
        return o;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: dotnet run -- fusion-validate --ha-history FILE --fusion-events FILE [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  --exclude-entity ID             binary_sensor to drop from occupancy ground truth (repeatable;");
        Console.Error.WriteLine("                                   default: binary_sensor.family_occupancy)");
        Console.Error.WriteLine("  --flap deviceId,startIso,endIso  labeled BLE-flapping window to score (repeatable)");
        Console.Error.WriteLine("  --latency-window-minutes F      max search window for latency matching (default 10)");
        Console.Error.WriteLine("  --output FILE                    write JSON report to FILE");
        Console.Error.WriteLine("  --markdown FILE                  write markdown report to FILE");
    }
}
