using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using ESPresense.Extensions;
using ESPresense.Utils;
using ESPresense.Models;
using ESPresense.Services;
using AutoMapper;
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
    private readonly IMapper _mapper;
    private readonly GlobalEventDispatcher _eventDispatcher;

    public StateController(ILogger<StateController> logger, State state, ConfigLoader config, NodeSettingsStore nsd, DeviceSettingsStore dss, NodeTelemetryStore nts, IMapper mapper, GlobalEventDispatcher eventDispatcher)
    {
        _logger = logger;
        _state = state;
        _config = config;
        _nsd = nsd;
        _dss = dss;
        _mapper = mapper;
        _eventDispatcher = eventDispatcher;
    }

    /// <summary>
    /// Enriches a single device with persisted settings like configured RSSI and friendly name.
    /// </summary>
    /// <param name="device">The device to enrich</param>
    private void EnrichConfiguredRefRssi(Device device)
    {
        var deviceSettings = _dss.Get(device.Id);
        device.ConfiguredRefRssi = deviceSettings?.RefRssi;
        if (!string.IsNullOrWhiteSpace(deviceSettings?.Name))
        {
            device.Name = deviceSettings.Name;
        }
    }

    /// <summary>
    /// Enriches multiple devices with ConfiguredRefRssi from DeviceSettings.
    /// </summary>
    /// <param name="devices">The devices to enrich</param>
    private void EnrichDevices(IEnumerable<Device> devices)
    {
        foreach (var device in devices)
        {
            EnrichConfiguredRefRssi(device);
        }
    }

    // GET: api/rooms
    [HttpGet("api/state/nodes")]
    public IEnumerable<NodeState> GetNodes(bool includeTele = true)
    {
        return includeTele ? _mapper.Map<IEnumerable<NodeStateTele>>(_state.Nodes.Values) : _mapper.Map<IEnumerable<NodeState>>(_state.Nodes.Values);
    }

    // GET: api/rooms
    [HttpGet("api/state/devices")]
    public IEnumerable<Device> GetDevices([FromQuery] bool showAll = false)
    {
        IEnumerable<Device> d = _state.Devices.Values;
        if (!showAll) d = d.Where(a => a is { Track: true });

        // Populate configured RefRssi from DeviceSettings
        EnrichDevices(d);

        return d;
    }

    // GET: api/config
    [HttpGet("api/state/config")]
    public Config GetConfig()
    {
        return _config.Config ?? new Config();
    }

    /// <summary>
    /// Retrieves calibration data and computes statistical measures based on active node distance measurements.
    /// </summary>
    /// <remarks>
    /// Iterates through active transmitter and receiver node pairs to populate a calibration matrix with various parameters,
    /// while collecting valid mapped and actual distance measurements. It then calculates the Pearson correlation coefficient (R)
    /// between mapped and actual distances and the root mean square error (RMSE) to quantify deviations. The results are returned
    /// in a Calibration object.
    /// </remarks>
    /// <returns>
    /// A Calibration object containing the calibration matrix along with computed values for R and RMSE.
    /// </returns>
    [HttpGet("api/state/calibration")]
    public Calibration GetCalibration()
    {
        var c = new Calibration();
        c.OptimizerState = _state.OptimizerState;

        // Lists to track all distance measurements for statistical calculations
        var mapDistances = new List<double>();
        var actualDistances = new List<double>();

        // Process traditional Node-to-Node relationships
        foreach (var (txId, tx) in _state.Nodes.Where(kv => kv.Value.RxNodes.Values.Any(n => n.Current)).OrderBy(a => a.Value.Name))
        {
            var txNs = _nsd.Get(txId);
            var txM = c.Matrix.GetOrAdd(tx.Name ?? txId);
            foreach (var (rxId, rx) in tx.RxNodes.Where(a => a.Value.Current).OrderBy(a => a.Value.Rx?.Name))
            {
                var rxNs = _nsd.Get(rxId);
                var rxM = txM.GetOrAdd(rx.Rx?.Name ?? rxId);
                if (txNs.Calibration.TxRefRssi is not null) rxM["tx_ref_rssi"] = txNs.Calibration.TxRefRssi.Value;
                if (rxNs.Calibration.RxAdjRssi is not null) rxM["rx_adj_rssi"] = rxNs.Calibration.RxAdjRssi.Value;
                if (rxNs.Calibration.Absorption is not null) rxM["absorption"] = rxNs.Calibration.Absorption.Value;
                rxM["mapDistance"] = rx.MapDistance;
                rxM["distance"] = rx.Distance;
                rxM["rssi"] = rx.Rssi;
                rxM["diff"] = rx.Distance - rx.MapDistance;
                rxM["percent"] = rx.MapDistance != 0 ? ((rx.Distance - rx.MapDistance) / rx.MapDistance) : 0;
                if (rx.DistVar is not null) rxM["var"] = rx.DistVar.Value;

                if (rx.MapDistance > 0 && rx.Distance > 0)
                {
                    mapDistances.Add(rx.MapDistance);
                    actualDistances.Add(rx.Distance);
                }
            }
        }

        // Process Anchored Devices as Transmitters
        foreach (var device in _state.Devices.Values.Where(d => d.IsAnchored && d.Anchor != null && d.Nodes.Values.Any(dn => dn.Current)))
        {
            var anchorName = device.Name ?? device.Id;
            c.Anchored.Add(anchorName);
            var txM = c.Matrix.GetOrAdd($"{anchorName}");

            foreach (var (rxId, deviceNode) in device.Nodes.Where(dn => dn.Value.Current && dn.Value.Node?.HasLocation == true))
            {
                var rxNode = deviceNode.Node!;
                var rxNs = _nsd.Get(rxNode.Id);
                var rxM = txM.GetOrAdd(rxNode.Name ?? rxNode.Id);

                // Anchored devices don't have tx calibration settings, but receivers still have their settings
                if (rxNs.Calibration.RxAdjRssi is not null) rxM["rx_adj_rssi"] = rxNs.Calibration.RxAdjRssi.Value;
                if (rxNs.Calibration.Absorption is not null) rxM["absorption"] = rxNs.Calibration.Absorption.Value;

                // Calculate map distance from anchor to receiver
                var mapDistance = device.Anchor!.Location.DistanceTo(rxNode.Location);
                rxM["mapDistance"] = mapDistance;
                rxM["distance"] = deviceNode.Distance;
                rxM["rssi"] = deviceNode.Rssi;
                rxM["diff"] = deviceNode.Distance - mapDistance;
                rxM["percent"] = mapDistance != 0 ? ((deviceNode.Distance - mapDistance) / mapDistance) : 0;
                if (deviceNode.DistVar is not null) rxM["var"] = deviceNode.DistVar.Value;

                if (mapDistance > 0 && deviceNode.Distance > 0)
                {
                    mapDistances.Add(mapDistance);
                    actualDistances.Add(deviceNode.Distance);
                }
            }
        }

        c.R = MathUtils.CalculatePearsonCorrelation(mapDistances, actualDistances);
        c.RMSE = MathUtils.CalculateRMSE(mapDistances, actualDistances);
        return c;
    }

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

        AsyncAutoResetEvent newMessage = new AsyncAutoResetEvent();
        ConcurrentQueue<string> changes = new ConcurrentQueue<string>();

        ConcurrentDictionary<string, bool> deviceSubscriptions = new ConcurrentDictionary<string, bool>();
        void EnqueueAndSignal<T>(T value)
        {
            changes.Enqueue(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            newMessage.Set();
        }
        void OnConfigChanged(object? sender, Config e) => EnqueueAndSignal(new { type = "configChanged" });
        void OnCalibrationChanged(object? sender, CalibrationEventArgs e) => EnqueueAndSignal(new { type = "calibrationChanged", data = e.Calibration });
        void OnNodeStateChanged(object? sender, NodeStateEventArgs e) => EnqueueAndSignal(new { type = "nodeStateChanged", data = e.NodeState });
        void OnDeviceRemoved(object? sender, DeviceRemovedEventArgs e) => EnqueueAndSignal(new { type = "deviceRemoved", deviceId = e.DeviceId });
        void OnDeviceChanged(object? sender, DeviceEventArgs e)
        {
            if (showAll || (e.Device?.Track ?? false) || e.TrackChanged)
            {
                // Enrich device with ConfiguredRefRssi before sending
                if (e.Device != null)
                    EnrichConfiguredRefRssi(e.Device);
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
                        var command = JsonSerializer.Deserialize<WebSocketCommand>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (command != null)
                        {
                            switch (command.Command?.ToLower())
                            {
                                case "changeFilter":
                                    if (command.Type == "showAll")
                                        showAll = command.Value == "true";
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
        }
    }

    /// <summary>
    /// Resets calibration settings for every node by setting TxRefRssi, RxAdjRssi, and Absorption to zero.
    /// </summary>
    /// <remarks>
    /// Iterates over all nodes, updates their calibration values to zero asynchronously, and returns an HTTP 200 response on success. If an exception occurs during the process, logs the error and returns an HTTP 500 response with an error message.
    /// </remarks>
    [HttpPost("api/state/calibration/reset")]
    public async Task<IActionResult> ResetCalibration()
    {
        try
        {
            // Reset calibration for all nodes
            foreach (var node in _state.Nodes.Values)
            {
                var nodeSettings = _nsd.Get(node.Id);
                nodeSettings.Calibration.TxRefRssi = 0;
                nodeSettings.Calibration.RxAdjRssi = 0;
                nodeSettings.Calibration.Absorption = 0;
                await _nsd.Set(node.Id, nodeSettings);
            }

            return Ok(new { message = "Calibration reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting calibration");
            return StatusCode(500, new { error = "An error occurred while resetting calibration" });
        }
    }
}

public class WebSocketCommand
{
    public string? Command { get; set; }
    public string? Type { get; set; }
    public string? Value { get; set; }
}
