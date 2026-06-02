using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ESPresense.Simulation.AccuracyHarness;

/// <summary>
/// Subcommand entry: <c>dotnet run -- accuracy</c>.
///
/// Modes:
///   <c>accuracy baseline</c>  → generate report (stdout or --output)
///   <c>accuracy check</c>     → re-generate and compare against the
///                                checked-in baseline within a numeric
///                                tolerance (CI regression guard).
/// </summary>
public static class AccuracyCli
{
    private const double DefaultTolerance = 0.05;
    private const string DefaultSeed = "20260530";

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "baseline":
                return CmdBaseline(args.Skip(1).ToArray());
            case "check":
                return CmdCheck(args.Skip(1).ToArray());
            default:
                PrintUsage();
                return 2;
        }
    }

    private static int CmdBaseline(string[] args)
    {
        var opts = ParseOpts(args);
        var scenarios = opts.Scenarios.Count == 0
            ? new List<string> { "baseline_4node_6x4m.json" }
            : opts.Scenarios;
        var locators = opts.Locators.Count == 0
            ? new List<LocatorKind> { LocatorKind.NelderMead }
            : opts.Locators;

        var profile = opts.NoiseProfile;
        var reports = new List<ScenarioReport>();
        foreach (var fname in scenarios)
        {
            var scn = HarnessScenario.Load(ResolveScenarioPath(fname));
            foreach (var locator in locators)
                reports.Add(AccuracyRunner.Run(scn, locator, opts.Seed, profile));
        }

        var json = AccuracyReport.ToJson(reports);
        var md = AccuracyReport.ToMarkdown(reports);

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
        return 0;
    }

    private static int CmdCheck(string[] args)
    {
        var opts = ParseOpts(args);
        var baselinePath = opts.BaselineJson ?? DefaultBaselineJsonPath();

        if (!File.Exists(baselinePath))
        {
            Console.Error.WriteLine($"check: baseline JSON not found at {baselinePath}");
            return 2;
        }

        var scenarios = opts.Scenarios.Count == 0
            ? new List<string> { "baseline_4node_6x4m.json" }
            : opts.Scenarios;
        var locators = opts.Locators.Count == 0
            ? new List<LocatorKind> { LocatorKind.NelderMead }
            : opts.Locators;

        var profile = opts.NoiseProfile;
        var current = new List<ScenarioReport>();
        foreach (var fname in scenarios)
        {
            var scn = HarnessScenario.Load(ResolveScenarioPath(fname));
            foreach (var locator in locators)
                current.Add(AccuracyRunner.Run(scn, locator, opts.Seed, profile));
        }

        var currentJson = AccuracyReport.ToJson(current);
        var baselineJson = File.ReadAllText(baselinePath);

        var diffs = CompareJson(baselineJson, currentJson, opts.Tolerance);
        if (diffs.Count == 0)
        {
            Console.Out.WriteLine($"Baseline match within ±{opts.Tolerance:P0} tolerance.");
            return 0;
        }

        Console.Error.WriteLine($"Accuracy baseline drift detected (tolerance ±{opts.Tolerance:P0}):");
        foreach (var d in diffs.Take(30))
            Console.Error.WriteLine($"  {d}");
        if (diffs.Count > 30)
            Console.Error.WriteLine($"  …and {diffs.Count - 30} more");

        Console.Error.WriteLine();
        Console.Error.WriteLine("If this drift is intentional, re-run the harness locally with:");
        Console.Error.WriteLine($"  dotnet run --project tests/ESPresense.Companion.Simulation -- accuracy baseline \\");
        Console.Error.WriteLine($"    --output {DefaultRelativeBaselineJsonPath()} \\");
        Console.Error.WriteLine($"    --markdown {DefaultRelativeBaselineMdPath()}");
        Console.Error.WriteLine("and commit the updated baseline + docs/accuracy.md with a PR description explaining the change.");
        return 1;
    }

    private sealed class Opts
    {
        public long Seed { get; set; } = long.Parse(DefaultSeed);
        public double GaussianStdDb { get; set; } = 2.5;
        public double MultipathProbability { get; set; } = 0.10;
        public double MultipathAttenuationDb { get; set; } = 6.0;
        public List<string> Scenarios { get; } = new();
        public List<LocatorKind> Locators { get; } = new();
        public string? JsonOutput { get; set; }
        public string? MarkdownOutput { get; set; }
        public string? BaselineJson { get; set; }
        public double Tolerance { get; set; } = DefaultTolerance;
        public NoiseProfile NoiseProfile => new()
        {
            GaussianStdDb = GaussianStdDb,
            MultipathProbability = MultipathProbability,
            MultipathAttenuationDb = MultipathAttenuationDb
        };
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
                case "--seed": o.Seed = long.Parse(Next()); break;
                case "--gaussian-std-db": o.GaussianStdDb = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--multipath-probability": o.MultipathProbability = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--multipath-attenuation-db": o.MultipathAttenuationDb = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--scenario": o.Scenarios.Add(Next()); break;
                case "--locator":
                    var lk = Next().Trim().ToLowerInvariant() switch
                    {
                        "neldermead" or "nelder-mead" or "nm" => LocatorKind.NelderMead,
                        "gaussnewton" or "gauss-newton" or "gn" => LocatorKind.GaussNewton,
                        "bfgs" => LocatorKind.Bfgs,
                        "mle" => LocatorKind.Mle,
                        var x => throw new ArgumentException($"unknown locator '{x}'")
                    };
                    o.Locators.Add(lk);
                    break;
                case "--output": o.JsonOutput = Next(); break;
                case "--markdown": o.MarkdownOutput = Next(); break;
                case "--baseline": o.BaselineJson = Next(); break;
                case "--tolerance": o.Tolerance = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown option '{a}'");
            }
        }
        return o;
    }

    private static string ResolveScenarioPath(string fname)
    {
        if (Path.IsPathRooted(fname) && File.Exists(fname)) return fname;
        var here = Path.GetDirectoryName(typeof(AccuracyCli).Assembly.Location)!;
        // Walk up from bin/Debug/net8.0 to the project dir
        var candidates = new[]
        {
            Path.Combine(here, "AccuracyHarness", "Scenarios", fname),
            Path.Combine(here, "Scenarios", fname),
            Path.Combine(AppContext.BaseDirectory, "AccuracyHarness", "Scenarios", fname),
            Path.Combine(FindRepoRoot(), "tests", "ESPresense.Companion.Simulation", "AccuracyHarness", "Scenarios", fname),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        throw new FileNotFoundException($"scenario file '{fname}' not found in any candidate location");
    }

    private static string DefaultBaselineJsonPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "AccuracyHarness", "Reports", "baseline-v1.json"),
            Path.Combine(FindRepoRoot(), "tests", "ESPresense.Companion.Simulation", "AccuracyHarness", "Reports", "baseline-v1.json"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return candidates[^1];
    }

    private static string DefaultRelativeBaselineJsonPath() =>
        "tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/baseline-v1.json";

    private static string DefaultRelativeBaselineMdPath() =>
        "tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/baseline-v1.md";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ESPresense-companion.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static List<string> CompareJson(string baseline, string current, double tolerance)
    {
        var diffs = new List<string>();
        var bDoc = JsonNode.Parse(baseline)!;
        var cDoc = JsonNode.Parse(current)!;
        WalkAndCompare("", bDoc, cDoc, tolerance, diffs);
        return diffs;
    }

    private static void WalkAndCompare(string path, JsonNode? a, JsonNode? b, double tol, List<string> diffs)
    {
        if (a is JsonObject ao && b is JsonObject bo)
        {
            var keys = new HashSet<string>(ao.Select(p => p.Key));
            keys.UnionWith(bo.Select(p => p.Key));
            foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
                WalkAndCompare(path + "/" + key, ao[key], bo[key], tol, diffs);
            return;
        }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count)
            {
                diffs.Add($"{path}: array length {aa.Count} → {ba.Count}");
                return;
            }
            for (int i = 0; i < aa.Count; i++)
                WalkAndCompare(path + $"[{i}]", aa[i], ba[i], tol, diffs);
            return;
        }
        if (a is JsonValue av && b is JsonValue bv)
        {
            // Identity / count fields (schema_version, base_seed, samples) are
            // emitted as integer JSON tokens. Compare those EXACTLY — a
            // regression guard should flag e.g. a drop in valid-solve count,
            // which the ±tolerance path could otherwise swallow. Only fractional
            // metrics fall through to the tolerance compare. (TryGetValue<long>
            // succeeds only when BOTH sides are integral, so a metric that
            // rounds to a whole number on one side still uses tolerance.)
            if (av.TryGetValue<long>(out var al) && bv.TryGetValue<long>(out var bl))
            {
                if (al != bl)
                    diffs.Add($"{path}: {al} → {bl} (exact-match field)");
                return;
            }
            // Numeric tolerance compare; otherwise exact string compare.
            if (av.TryGetValue<double>(out var ad) && bv.TryGetValue<double>(out var bd))
            {
                if (!CloseEnough(ad, bd, tol))
                    diffs.Add($"{path}: {ad:F3} → {bd:F3} (Δ={Math.Abs(ad - bd):F3}, allowed ±{Math.Max(tol * Math.Abs(ad), 0.01):F3})");
                return;
            }
            if (av.ToJsonString() != bv.ToJsonString())
                diffs.Add($"{path}: {av} → {bv}");
            return;
        }
        if (a == null && b == null) return;
        diffs.Add($"{path}: {(a?.ToString() ?? "<null>")} → {(b?.ToString() ?? "<null>")}");
    }

    private static bool CloseEnough(double baseline, double current, double tol)
    {
        if (double.IsNaN(baseline) && double.IsNaN(current)) return true;
        if (double.IsNaN(baseline) || double.IsNaN(current)) return false;
        if (baseline == current) return true;
        // Relative tolerance with a small absolute floor (0.01 m / 0.01 absolute units)
        // so near-zero baselines don't flag on subnoise drift across runners.
        double allowed = Math.Max(tol * Math.Abs(baseline), 0.01);
        return Math.Abs(current - baseline) <= allowed;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: dotnet run -- accuracy <baseline|check> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Common options:");
        Console.Error.WriteLine("  --seed N                       deterministic seed (default 20260530)");
        Console.Error.WriteLine("  --scenario FILE                scenario filename under Scenarios/ (repeatable)");
        Console.Error.WriteLine("  --locator NelderMead|GaussNewton|BFGS|MLE (repeatable; default NelderMead)");
        Console.Error.WriteLine("  --gaussian-std-db F            RSSI Gaussian σ in dB (default 2.5)");
        Console.Error.WriteLine("  --multipath-probability F      multipath burst probability (default 0.10)");
        Console.Error.WriteLine("  --multipath-attenuation-db F   multipath -dB hit (default 6.0)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("baseline options:");
        Console.Error.WriteLine("  --output FILE                  write JSON report to FILE");
        Console.Error.WriteLine("  --markdown FILE                write markdown report to FILE");
        Console.Error.WriteLine();
        Console.Error.WriteLine("check options:");
        Console.Error.WriteLine("  --baseline FILE                checked-in baseline JSON to compare against");
        Console.Error.WriteLine("  --tolerance F                  relative tolerance (default 0.05 = 5%)");
    }
}
