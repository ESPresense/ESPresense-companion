using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;
using SQLite;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace ESPresense.Controllers
{
    [Route("api/device")]
    [ApiController]
    public class DeviceController : ControllerBase
    {
        private readonly SQLiteConnection _db;
        private readonly ILogger<RoomController> _logger;
        private readonly DeviceSettingsStore _deviceSettingsStore;

        public DeviceController(SQLiteConnection db, ILogger<RoomController> logger, DeviceSettingsStore deviceSettingsStore)
        {
            _db = db;
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
        }

        [HttpGet("{id}")]
        public async Task<DeviceSettings?> Get(string id)
        {
            return await _deviceSettingsStore.Get(id);
        }

        [HttpPut("{id}")]
        public async Task Set(string id, [FromBody] DeviceSettings value)
        {
            await _deviceSettingsStore.Set(id, value);
        }
    }
}
