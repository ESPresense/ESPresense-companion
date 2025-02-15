using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api/device")]
    [ApiController]
    public class DeviceController : ControllerBase
    {
        private readonly ILogger<DeviceController> _logger;
        private readonly DeviceSettingsStore _deviceSettingsStore;
        private readonly State _state;

        public DeviceController(ILogger<DeviceController> logger, DeviceSettingsStore deviceSettingsStore, State state)
        {
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
            _state = state;
        }

        [HttpGet("{id}/details")]
        public async Task<IList<KeyValuePair<string, string>>> Details(string id)
        {
            if (_state.Devices.TryGetValue(id, out var device))
                return device.GetDetails().ToList();
            return new List<KeyValuePair<string, string>>();
        }

        [HttpGet("{id}/settings")]
        public Task<DeviceSettings?> Get(string id)
        {
            var settings = _deviceSettingsStore.Get(id);
            settings ??= new DeviceSettings { OriginalId = id, Id = id };
            return Task.FromResult(settings);
        }

        [HttpPut("{id}/settings")]
        public async Task Set(string id, [FromBody] DeviceSettings value)
        {
            await _deviceSettingsStore.Set(id, value);
        }
    }
}
