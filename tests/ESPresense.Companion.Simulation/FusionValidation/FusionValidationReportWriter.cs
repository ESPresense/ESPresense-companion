using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ESPresense.Simulation.FusionValidation;

public static class FusionValidationReportWriter
{
    public static string ToJson(ValidationReport r)
    {
        var root = new JsonObject
        {
            ["flap_resolution_rate"] = Round(r.FlapResolutionRate),
            ["flap_samples"] = r.FlapSamples,
            ["attribution_accuracy"] = Round(r.AttributionAccuracy),
            ["attribution_samples"] = r.AttributionSamples,
            ["false_unknown_rate"] = Round(r.FalseUnknownRate),
            ["unknown_events"] = r.UnknownEvents,
            ["latency"] = new JsonArray(r.Latencies.Select(l => (JsonNode)new JsonObject
            {
                ["room"] = l.Room,
                ["ha_transition_at"] = l.HaTransitionAt.ToString("O", CultureInfo.InvariantCulture),
                ["fusion_transition_at"] = l.FusionTransitionAt?.ToString("O", CultureInfo.InvariantCulture),
                ["latency_seconds"] = l.Latency?.TotalSeconds
            }).ToArray())
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        return root.ToJsonString(options) + "\n";
    }

    public static string ToMarkdown(ValidationReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Fusion validation vs HA history");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value | Samples |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| Flap resolution rate | {Pct(r.FlapResolutionRate)} | {r.FlapSamples} |");
        sb.AppendLine($"| Attribution accuracy | {Pct(r.AttributionAccuracy)} | {r.AttributionSamples} |");
        sb.AppendLine($"| False unknown-occupant rate | {Pct(r.FalseUnknownRate)} | {r.UnknownEvents} |");
        sb.AppendLine();
        sb.AppendLine("Latency (fused occupied-start vs HA occupancy-start; negative = fusion/CSI led HA):");
        sb.AppendLine();
        sb.AppendLine("| Room | HA transition | Fusion transition | Latency (s) |");
        sb.AppendLine("|---|---|---|---:|");
        foreach (var l in r.Latencies)
        {
            var fusion = l.FusionTransitionAt?.ToString("O", CultureInfo.InvariantCulture) ?? "(no match)";
            var latency = l.Latency is null ? "—" : l.Latency.Value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {l.Room} | {l.HaTransitionAt:O} | {fusion} | {latency} |");
        }
        return sb.ToString();
    }

    private static double Round(double v) => double.IsNaN(v) ? v : Math.Round(v, 3, MidpointRounding.AwayFromZero);

    private static string Pct(double v) => double.IsNaN(v) ? "n/a" : (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
}
