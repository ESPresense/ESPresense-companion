using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

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
    public IEnumerable<Device> GetDevices()
    {
        return _state.Devices.Values.Where(a => a is { Track: true });
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
                if (txNs.TxRefRssi is not null) rxM["tx_ref_rssi"] = txNs.TxRefRssi.Value;
                if (rxNs.RxAdjRssi is not null) rxM["rx_adj_rssi"] = rxNs.RxAdjRssi.Value;
                if (rxNs.Absorption is not null) rxM["absorption"] = rxNs.Absorption.Value;
                rxM["expected"] = rx.Expected;
                rxM["actual"] = rx.Distance;
                rxM["rssi"] = rx.Rssi;
                rxM["err"] = rx.Expected - rx.Distance;
                rxM["percent"] = rx.Distance / rx.Expected;
                if (rx.Variance is not null) rxM["var"] = rx.Variance.Value;
            }
        }

        return c;
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [Route("/ws")]
    public async Task Get()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        ConcurrentQueue<string> changes = new ConcurrentQueue<string>();
        void OnConfigChanged(object? sender, Config e) => changes.Enqueue(JsonSerializer.Serialize(new { type = "configChanged" }));
        void OnCalibrationChanged(object? sender, CalibrationEventArgs e) => changes.Enqueue(JsonSerializer.Serialize(new { type = "calibrationChanged", data = e.Calibration }));
        void OnNodeStateChanged(object? sender, NodeStateEventArgs e) => changes.Enqueue(JsonSerializer.Serialize(new { type = "nodeStateChanged", data = e.NodeState }));
        void OnDeviceChanged(object? sender, DeviceEventArgs e) => changes.Enqueue(JsonSerializer.Serialize(new { type = "deviceChanged", data = e.Device }));

        _config.ConfigChanged += OnConfigChanged;
        _eventDispatcher.CalibrationChanged += OnCalibrationChanged;
        _eventDispatcher.NodeStateChanged += OnNodeStateChanged;
        _eventDispatcher.DeviceStateChanged += OnDeviceChanged;
        try
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

            while (!webSocket.CloseStatus.HasValue)
            {
                while (changes.TryDequeue(out var jsonEvent))
                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonEvent)), WebSocketMessageType.Text, true, CancellationToken.None);

                await Task.Delay(100);
            }

            await webSocket.CloseAsync(webSocket.CloseStatus.Value, webSocket.CloseStatusDescription, CancellationToken.None);
        }
        finally
        {
            _config.ConfigChanged -= OnConfigChanged;
            _eventDispatcher.CalibrationChanged -= OnCalibrationChanged;
            _eventDispatcher.NodeStateChanged -= OnNodeStateChanged;
            _eventDispatcher.DeviceStateChanged -= OnDeviceChanged;
        }
    }
}


