using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Extensions;
using ESPresense.Utils;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using Nito.AsyncEx;
using ESPresense.Events;
using JsonSerializer = System.Text.Json.JsonSerializer;

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
    private readonly GlobalEventDispatcher _eventDispatcher;
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
        _eventDispatcher = eventDispatcher;
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
        string? include = null; // set by the changeFilter command
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        AsyncAutoResetEvent newMessage = new AsyncAutoResetEvent();
        ConcurrentQueue<string> changes = new ConcurrentQueue<string>();

        ConcurrentDictionary<string, bool> deviceSubscriptions = new ConcurrentDictionary<string, bool>();
        void EnqueueAndSignal<T>(T value)
        {
            changes.Enqueue(JsonSerializer.Serialize(value, JsonDefaults.WebSocket));
            newMessage.Set();
        }
        void OnConfigChanged(object? sender, Config e) => EnqueueAndSignal(new { type = "configChanged" });
        void OnCalibrationChanged(object? sender, CalibrationEventArgs e) => EnqueueAndSignal(new { type = "calibrationChanged", data = e.Calibration });
        void OnNodeStateChanged(object? sender, NodeStateEventArgs e) => EnqueueAndSignal(new { type = "nodeStateChanged", data = e.NodeState });
        void OnDeviceRemoved(object? sender, DeviceRemovedEventArgs e) => EnqueueAndSignal(new { type = "deviceRemoved", deviceId = e.DeviceId });
        void OnLocatorStateChanged(object? sender, LocatorStateEventArgs e) => EnqueueAndSignal(new { type = "locatorStateChanged", data = e.LocatorState });
        void OnDeviceChanged(object? sender, DeviceEventArgs e)
        {
            if (showAll || (e.Device?.Track ?? false) || e.TrackChanged || IsIncluded(e.Device?.Id, include))
            {
                // Enrich device with ConfiguredRefRssi before sending
                if (e.Device != null)
                    _enricher.Enrich(e.Device);
                EnqueueAndSignal(new { type = "deviceChanged", data = e.Device });
            }
        }

        void OnDeviceMessageReceived(object? sender, DeviceMessageEventArgs args)
        {
            if (deviceSubscriptions.TryGetValue(args.DeviceId, out var isSubscribed) && isSubscribed)
            {
                EnqueueAndSignal(new
                {
                    type = "deviceMessage",
                    deviceId = args.DeviceId,
                    nodeId = args.NodeId,
                    data = args.Payload
                });
            }
        }

        _config.ConfigChanged += OnConfigChanged;
        _eventDispatcher.CalibrationChanged += OnCalibrationChanged;
        _eventDispatcher.NodeStateChanged += OnNodeStateChanged;
        _eventDispatcher.DeviceStateChanged += OnDeviceChanged;
        _eventDispatcher.DeviceMessageReceived += OnDeviceMessageReceived;
        _eventDispatcher.DeviceRemoved += OnDeviceRemoved;
        _eventDispatcher.LocatorStateChanged += OnLocatorStateChanged;

        try
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

            // Set up buffer for receiving messages
            var buffer = new byte[1024 * 4];

            var receiveTask = Task.Run(async () =>
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    using var ms = new MemoryStream();
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    ms.Seek(0, SeekOrigin.Begin);

                    // Process the command
                    try
                    {
                        using var reader = new StreamReader(ms, Encoding.UTF8);
                        var message = await reader.ReadToEndAsync();
                        var command = JsonSerializer.Deserialize<WebSocketCommand>(message, JsonDefaults.WebSocket);

                        if (command != null)
                        {
                            switch (command.Command?.ToLower())
                            {
                                case "changefilter":
                                    if (command.Type == "showAll")
                                        showAll = command.Value == "true";
                                    else if (command.Type == "include")
                                        include = command.Value;
                                    break;
                                case "subscribe":
                                    if (command.Type == "deviceMessage" && !string.IsNullOrEmpty(command.Value))
                                    {
                                        deviceSubscriptions.AddOrUpdate(command.Value, true, (_, __) => true);
                                        _logger.LogDebug("Client subscribed to device messages for {DeviceId}", command.Value);
                                    }
                                    break;

                                case "unsubscribe":
                                    if (command.Type == "deviceMessage" && !string.IsNullOrEmpty(command.Value))
                                    {
                                        deviceSubscriptions.TryRemove(command.Value, out _);
                                        _logger.LogDebug("Client unsubscribed from device messages for {DeviceId}", command.Value);
                                    }
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing WebSocket command");
                    }
                }
            });

            EnqueueAndSignal(new { type = "time", data = DateTime.UtcNow.RelativeMilliseconds() });

            while (webSocket.State == WebSocketState.Open)
            {
                while (changes.TryDequeue(out var jsonEvent))
                {
                    try
                    {
                        await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonEvent)), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.InvalidState)
                    {
                        _logger.LogDebug("WebSocket send failed as client disconnected: {CloseStatus}", webSocket.CloseStatus);
                    }
                }

                if (webSocket.State == WebSocketState.Open)
                    await newMessage.WaitAsync();
            }

            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, webSocket.CloseStatusDescription, CancellationToken.None);
        }
        finally
        {
            _config.ConfigChanged -= OnConfigChanged;
            _eventDispatcher.CalibrationChanged -= OnCalibrationChanged;
            _eventDispatcher.NodeStateChanged -= OnNodeStateChanged;
            _eventDispatcher.DeviceStateChanged -= OnDeviceChanged;
            _eventDispatcher.DeviceMessageReceived -= OnDeviceMessageReceived;
            _eventDispatcher.DeviceRemoved -= OnDeviceRemoved;
            _eventDispatcher.LocatorStateChanged -= OnLocatorStateChanged;
        }
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
