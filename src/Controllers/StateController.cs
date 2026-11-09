using ESPresense.Utils;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[ApiController]
public class StateController : ControllerBase
{
    private readonly ILogger<StateController> _logger;
    private readonly State _state;
    private readonly ConfigLoader _config;
    private readonly NodeSettingsStore _nsd;
    private readonly DeviceSettingsStore _dss;
    private readonly NodeStateMapper _mapper;
    private readonly CalibrationService _calibration;
    private readonly DeviceEnricher _enricher;

    public StateController(ILogger<StateController> logger, State state, ConfigLoader config, NodeSettingsStore nsd, DeviceSettingsStore dss, NodeTelemetryStore nts, NodeStateMapper mapper, GlobalEventDispatcher eventDispatcher, CalibrationService calibration, DeviceEnricher enricher)
    {
        _enricher = enricher;
        _calibration = calibration;
        _logger = logger;
        _state = state;
        _config = config;
        _nsd = nsd;
        _dss = dss;
        _mapper = mapper;
    }

    // GET: api/rooms
    [HttpGet("api/state/nodes")]
    public IEnumerable<NodeState> GetNodes(bool includeTele = true)
    {
        if (includeTele) return _mapper.ToNodeStateTeles(_state.Nodes.Values);
        return NodeStateMapper.ToNodeStates(_state.Nodes.Values);
    }

    // GET: api/rooms
    [HttpGet("api/state/devices")]
    public IEnumerable<Device> GetDevices([FromQuery] bool showAll = false, [FromQuery] string? include = null)
    {
        IEnumerable<Device> d = _state.Devices.Values;
        if (!showAll) d = d.Where(a => a.Track || IsIncluded(a.Id, include));

        // Populate configured RefRssi from DeviceSettings
        _enricher.Enrich(d);

        return d;
    }

    /// <summary>An untracked device the client has open, which it wants despite the showAll filter.</summary>
    private static bool IsIncluded(string? id, string? include) => !string.IsNullOrEmpty(include) && string.Equals(id, include, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Searches everything known (nodes and devices, tracked or not) by id, name, room or floor. Terms are
    /// AND-ed and a MAC matches with or without separators. Returns at most <paramref name="limit"/> ranked
    /// results. An empty query lists devices only: nodes would otherwise fill the list ahead of them.
    /// </summary>
    [HttpGet("api/state/search")]
    public IEnumerable<SearchResult> SearchAll([FromQuery] string? q = null, [FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var items = _state.Devices.Values.Select(d =>
        {
            var name = _dss.Get(d.Id)?.Name;
            return Search.Device(d, string.IsNullOrWhiteSpace(name) ? d.Name : name);
        });
        if (!string.IsNullOrWhiteSpace(q)) items = items.Concat(_mapper.ToNodeStateTeles(_state.Nodes.Values).Select(Search.Node));
        return Search.Find(items, q, limit);
    }

    // GET: api/config
    [HttpGet("api/state/config")]
    public Config GetConfig()
    {
        return _config.Config ?? new Config();
    }

    [HttpGet("api/state/locator")]
    public IActionResult GetLocator()
    {
        return Ok(_state.LocatorState);
    }

    /// <summary>
    /// The calibration matrix (node pairs and anchored devices) with R and RMSE. Built by <see cref="CalibrationService"/>,
    /// which also backs the MCP <c>get_calibration</c> tool.
    /// </summary>
    [HttpGet("api/state/calibration")]
    public Calibration GetCalibration() => _calibration.Calculate();

    /// <summary>
    /// Opens a WebSocket connection to stream real-time state updates.
    /// </summary>
    /// <remarks>
    /// This asynchronous method verifies that the request is a valid WebSocket connection and, if so, establishes the connection.
    /// It registers event handlers to deliver updates for configuration, calibration, node state, and device changes.
    /// Additionally, the method processes incoming commands to adjust filtering and device message subscriptions based on client input.
    /// If the request is not a valid WebSocket request, it responds with a 400 Bad Request status.
    /// </remarks>
    /// <param name="showAll">
    /// Determines whether to include all device updates (true) or only updates for tracked devices (false).
    /// </param>
    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("/ws")]
    public async Task Get([FromQuery] bool showAll = false)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var session = HttpContext.RequestServices.GetRequiredService<StateWebSocketSession>();
        await session.RunAsync(socket, showAll, HttpContext.RequestAborted);
    }

    /// <summary>
    /// Resets every node's TxRefRssi, RxAdjRssi and Absorption to the node's own defaults.
    /// </summary>
    /// <remarks>
    /// Returns HTTP 200 on success; logs the error and returns HTTP 500 if a reset fails.
    /// </remarks>
    [HttpPost("api/state/calibration/reset")]
    public async Task<IActionResult> ResetCalibration()
    {
        try
        {
            // Reset calibration for all nodes
            foreach (var node in _state.Nodes.Values)
                await _nsd.ResetCalibration(node.Id);

            _state.ResetCalibrationMetrics();

            return Ok(new { message = "Calibration reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting calibration");
            return StatusCode(500, new { error = "An error occurred while resetting calibration" });
        }
    }

    [HttpGet("api/state/calibration/auto-optimize")]
    public IActionResult GetAutoOptimize()
    {
        var c = _config.Config;
        return Ok(new { autoOptimize = c?.Optimization.Enabled ?? false });
    }

    [HttpPost("api/state/calibration/auto-optimize")]
    public async Task<IActionResult> ToggleAutoOptimize([FromBody] bool enable)
    {
        try
        {
            var c = _config.Config;
            if (c == null) return StatusCode(500, new { error = "Config not loaded" });

            // Save a modified copy: the loaded Config is the instance State holds, and only a reload may replace it.
            var optimization = c.Optimization.Clone();
            optimization.Enabled = enable;
            await _config.SaveSectionAsync("optimization", optimization);
            return Ok(new { autoOptimize = enable });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save auto-optimize setting");
            return StatusCode(500, new { error = "Failed to save auto-optimize setting" });
        }
    }
}

public class WebSocketCommand
{
    public string? Command { get; set; }
    public string? Type { get; set; }
    public string? Value { get; set; }
}
