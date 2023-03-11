using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

        public StateController(ILogger<StateController> logger, State state, ConfigLoader config)
        {
            _logger = logger;
            _state = state;
            _config = config;
        }

        // GET: api/rooms
        [HttpGet("api/state/nodes")]
        public IEnumerable<Node> GetNodes()
        {
            return _state.Nodes.Values;
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
