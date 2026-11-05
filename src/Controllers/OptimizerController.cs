using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

/// <summary>
/// Optimizer debugging (#1857). Built by <see cref="OptimizerDebugService"/>, which also backs the MCP
/// <c>get_optimizer_*</c> and <c>run_optimizer_dry_cycle</c> tools. Nothing here changes settings.
/// </summary>
[ApiController]
public class OptimizerController(OptimizerDebugService service) : ControllerBase
{
    [HttpGet("api/optimizer/status")]
    public OptimizerStatus GetStatus() => service.GetStatus();

    [HttpGet("api/optimizer/last-cycle")]
    public ActionResult<OptimizerCycleReport> GetLastCycle()
        => service.GetLastCycle() is { } report ? report : NotFound(new { ok = false, error = "No optimization cycle has run since start-up." });

    [HttpGet("api/optimizer/residuals")]
    public async Task<ActionResult<OptimizerResidualsReport>> GetResiduals([FromQuery] bool dryRun = false, [FromQuery] string? optimizer = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await service.GetResidualsAsync(dryRun, optimizer, cancellationToken) is { } residuals
                ? residuals
                : NotFound(new { ok = false, error = "No residuals: no cycle with proposals has run, or it was skipped for too few validation measurements." });
        }
        catch (UnknownOptimizerException ex)
        {
            return BadRequest(new { ok = false, error = ex.Message });
        }
        catch (OptimizerUnavailableException ex)
        {
            return Conflict(new { ok = false, error = ex.Message });
        }
    }

    [HttpGet("api/optimizer/measures")]
    public OptimizerMeasuresReport GetMeasures([FromQuery] string? nodeId = null) => service.GetMeasures(nodeId);

    /// <summary>Runs one cycle on the current history and reports it. A POST because it does work, but it applies nothing.</summary>
    [HttpPost("api/optimizer/dry-run")]
    public async Task<ActionResult<OptimizerCycleReport>> DryRun([FromQuery] string? optimizer = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await service.RunDryCycleAsync(optimizer, cancellationToken);
        }
        catch (UnknownOptimizerException ex)
        {
            return BadRequest(new { ok = false, error = ex.Message });
        }
        catch (OptimizerUnavailableException ex)
        {
            return Conflict(new { ok = false, error = ex.Message });
        }
    }
}
