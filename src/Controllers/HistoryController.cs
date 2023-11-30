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
        private readonly DatabaseFactory _databaseFactory;

        public HistoryController(ILogger<DeviceController> logger, DeviceSettingsStore deviceSettingsStore, State state, DatabaseFactory databaseFactory)
        {
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
            _state = state;
            _databaseFactory = databaseFactory;
        }

        [HttpGet("{id}")]
        public async Task<DeviceHistoryResponse> Get(string id)
        {
            var dh = await _databaseFactory.GetDeviceHistory();
            var history = await dh.List(id);
            return new DeviceHistoryResponse(history);
        }
    }

    public  record DeviceHistoryResponse(IList<DeviceHistory> history);

}
