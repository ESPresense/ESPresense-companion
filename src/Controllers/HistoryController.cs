using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api/history")]
    [ApiController]
    public class HistoryController : ControllerBase
    {
        private readonly ILogger<DeviceController> _logger;
        private readonly DeviceSettingsStore _deviceSettingsStore;
        private readonly State _state;
        private readonly DeviceHistoryStore _deviceHistoryStore;

        public HistoryController(ILogger<DeviceController> logger, DeviceSettingsStore deviceSettingsStore, State state, DeviceHistoryStore deviceHistoryStore)
        {
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
            _state = state;
            _deviceHistoryStore = deviceHistoryStore;
        }

        [HttpGet("{id}")]
        public async Task<DeviceHistoryResponse> Get(string id)
        {
            var history = await _deviceHistoryStore.List(id) ?? new List<DeviceHistory>();
            return new DeviceHistoryResponse(history);
        }

        [HttpGet("{id}/range")]
        public async Task<DeviceHistoryResponse> GetRange(string id, [FromQuery] DateTime start, [FromQuery] DateTime end)
        {
            var history = await _deviceHistoryStore.List(id, start, end) ?? new List<DeviceHistory>(); // Return empty list if null
            return new DeviceHistoryResponse(history);
        }

    }

    public  record DeviceHistoryResponse(IList<DeviceHistory> history);

}
