using System.ComponentModel;
using System.Text.Json;
using ESPresense.Utils;
using ModelContextProtocol.Server;

namespace ESPresense.Services;

/// <summary>
/// MCP tools for debugging the calibration optimizer (#1857), backed by <see cref="OptimizerDebugService"/> like
/// /api/optimizer, and serialized with the REST settings so each returns the same JSON as its endpoint.
/// None of them changes anything: the dry run scores proposals without applying them.
/// </summary>
[McpServerToolType]
public class McpOptimizerTools(OptimizerDebugService service)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Web;

    private static string Error(string error) => JsonSerializer.Serialize(new { ok = false, error }, Json);

    [McpServerTool(Name = "get_optimizer_status", ReadOnly = true)]
    [Description("Optimizer status: phase, lease holder and expiry, enabled optimizers, snapshot count and time span, training/validation split sizes, last and next run, last outcome, and the gate settings.")]
    public string GetOptimizerStatus() => JsonSerializer.Serialize(service.GetStatus(), Json);

    [McpServerTool(Name = "get_optimizer_last_cycle", ReadOnly = true)]
    [Description("The last optimization cycle: baseline composite/R/RMSE on the held-out eval set, and per optimizer its per-node proposal (absorption, rxAdjRssi, txRefRssi), its composite/R/RMSE, and the gate decision with the numbers behind every gate (min samples, improvement, device frame with txRef held, eval snapshots lost on) and whether it was applied.")]
    public string GetOptimizerLastCycle()
        => service.GetLastCycle() is { } report
            ? JsonSerializer.Serialize(report, Json)
            : Error("No optimization cycle has run since start-up.");

    [McpServerTool(Name = "get_optimizer_residuals", ReadOnly = true)]
    [Description("Per node, as receiver and as transmitter: count, mean and RMS residual (measured minus predicted RSSI, dB) on the eval set, under the incumbent settings and under each proposal (and, for proposals that move txRef, with txRef held). Shows which nodes a proposal fixes and which it breaks. From the last cycle, or from a dry cycle run now.")]
    public async Task<string> GetOptimizerResiduals(
        [Description("Run a dry cycle now on the current history instead of reporting the last cycle")] bool dryRun = false,
        [Description("With dryRun, only this optimizer (by name)")] string? optimizer = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await service.GetResidualsAsync(dryRun, optimizer, cancellationToken) is { } residuals
                ? JsonSerializer.Serialize(residuals, Json)
                : Error(dryRun
                    ? "The dry cycle was skipped: too few validation measurements (min_samples)."
                    : "No optimization cycle with proposals has run since start-up.");
        }
        catch (Exception ex) when (ex is OptimizerUnavailableException or UnknownOptimizerException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "get_optimizer_measures", ReadOnly = true)]
    [Description("The pooled training snapshot the optimizers fit, one cell per tx/rx pair: samples, mean RSSI, its spread, the rxAdj values it was taken with, refRssi, rssiVar, map and measured distance, and whether the transmitter is an anchored device.")]
    public string GetOptimizerMeasures(
        [Description("Only the pairs this node is on, as transmitter or receiver")] string? nodeId = null)
        => JsonSerializer.Serialize(service.GetMeasures(nodeId), Json);

    [McpServerTool(Name = "run_optimizer_dry_cycle", ReadOnly = true)]
    [Description("Run one optimization cycle now on the current history and return the same report as get_optimizer_last_cycle, applying nothing: no node settings are changed and nothing is published.")]
    public async Task<string> RunOptimizerDryCycle(
        [Description("Only this optimizer, by name, e.g. \"Per Node Absorption Rx Tx Adj\"; default the configured set")] string? optimizer = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return JsonSerializer.Serialize(await service.RunDryCycleAsync(optimizer, cancellationToken), Json);
        }
        catch (Exception ex) when (ex is OptimizerUnavailableException or UnknownOptimizerException)
        {
            return Error(ex.Message);
        }
    }
}
