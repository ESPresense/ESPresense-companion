using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace ESPresense.Controllers
{
    [ApiController]
    public class StateController : ControllerBase
    {
        private readonly ILogger<StateController> _logger;
        private readonly State _state;
        private readonly ConfigLoader _config;
        private readonly NodeSettingsStore _nsd;
        private readonly MappingService _ms;

        public StateController(ILogger<StateController> logger, State state, ConfigLoader config, NodeSettingsStore nsd, NodeTelemetryStore nts, MappingService ms)
        {
            _logger = logger;
            _state = state;
            _config = config;
            _nsd = nsd;
            _ms = ms;
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

            bool configChanged = true;

            void OnConfigChanged(object? sender, Config args) => configChanged = true;

            _config.ConfigChanged += OnConfigChanged;
            try
            {
                using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();

                while (!webSocket.CloseStatus.HasValue)
                {
                    await Task.Delay(100);
                    if (configChanged)
                    {
                        configChanged = false;
                        var json = JsonSerializer.Serialize(new { type = "configChanged" });
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }

                await webSocket.CloseAsync(
                    webSocket.CloseStatus.Value,
                    webSocket.CloseStatusDescription,
                    CancellationToken.None);
            }
            finally
            {
                _config.ConfigChanged -= OnConfigChanged;
            }
        }
    }
}
