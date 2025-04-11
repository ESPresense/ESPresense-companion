﻿using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;
using ESPresense.Extensions;
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
    private readonly MappingService _ms;
    private readonly GlobalEventDispatcher _eventDispatcher;

    public StateController(ILogger<StateController> logger, State state, ConfigLoader config, NodeSettingsStore nsd, NodeTelemetryStore nts, MappingService ms, GlobalEventDispatcher eventDispatcher)
    {
        _logger = logger;
        _state = state;
        _config = config;
        _nsd = nsd;
        _ms = ms;
        _eventDispatcher = eventDispatcher;
    }

    // GET: api/rooms
    [HttpGet("api/state/nodes")]
    public IEnumerable<NodeState> GetNodes(bool includeTele = true)
    {
        return includeTele ? _ms.Mapper.Map<IEnumerable<NodeStateTele>>(_state.Nodes.Values) : _ms.Mapper.Map<IEnumerable<NodeState>>(_state.Nodes.Values);
    }

    // GET: api/rooms
    [HttpGet("api/state/devices")]
    public IEnumerable<Device> GetDevices([FromQuery] bool showAll = false)
    {
        IEnumerable<Device> d = _state.Devices.Values;
        if (!showAll) d = d.Where(a => a is { Track: true });
        return d;
    }

    // GET: api/config
    [HttpGet("api/state/config")]
    public Config GetConfig()
    {
        return _config.Config ?? new Config();
    }

    [HttpGet("api/state/calibration")]
    public Calibration GetCalibration()
    {
        var c = new Calibration();
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
            }
        }

        return c;
    }

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
            changes.Enqueue(JsonSerializer.Serialize(value, new JsonSerializerOptions (JsonSerializerDefaults.Web)));
            newMessage.Set();
        }
        void OnConfigChanged(object? sender, Config e) => EnqueueAndSignal(new { type = "configChanged" });
        void OnCalibrationChanged(object? sender, CalibrationEventArgs e) => EnqueueAndSignal(new { type = "calibrationChanged", data = e.Calibration });
        void OnNodeStateChanged(object? sender, NodeStateEventArgs e) => EnqueueAndSignal(new { type = "nodeStateChanged", data = e.NodeState });
        void OnDeviceChanged(object? sender, DeviceEventArgs e)
        {
            if (showAll || (e.Device?.Track ?? false) || e.TrackChanged)
                EnqueueAndSignal(new { type = "deviceChanged", data = e.Device });
        };

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

            while (!webSocket.CloseStatus.HasValue)
            {
                while (changes.TryDequeue(out var jsonEvent))
                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonEvent)), WebSocketMessageType.Text, true, CancellationToken.None);

                await newMessage.WaitAsync();
            }

            await webSocket.CloseAsync(webSocket.CloseStatus.Value, webSocket.CloseStatusDescription, CancellationToken.None);
        }
        finally
        {
            _config.ConfigChanged -= OnConfigChanged;
            _eventDispatcher.CalibrationChanged -= OnCalibrationChanged;
            _eventDispatcher.NodeStateChanged -= OnNodeStateChanged;
            _eventDispatcher.DeviceStateChanged -= OnDeviceChanged;
            _eventDispatcher.DeviceMessageReceived -= OnDeviceMessageReceived;
        }
    }

    [HttpPost("api/state/calibration/reset")]
    public async Task<IActionResult> ResetCalibration()
    {
        try
        {
            // Reset calibration for all nodes
            foreach (var node in _state.Nodes.Values)
            {
                var nodeSettings = _nsd.Get(node.Id);
                nodeSettings.Calibration.TxRefRssi = null;
                nodeSettings.Calibration.RxAdjRssi = null;
                nodeSettings.Calibration.Absorption = null;
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

