using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers
{
    [Route("api")]
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

        [HttpGet("devices")]
        public IEnumerable<DeviceSettings> Get()
        {
            return _deviceSettingsStore.GetAll();
        }

        [HttpGet("device/{id}")]
        public DeviceSettingsDetails Get(string id, [FromQuery] bool byAlias = false)
        {
            var deviceSettings = _deviceSettingsStore.Get(id, byAlias);
            var details = new List<KeyValuePair<string, string>>();
            if (deviceSettings?.Id != null && _state.Devices.TryGetValue(deviceSettings.Id, out var device))
                details.AddRange(device.GetDetails());
            return new DeviceSettingsDetails(deviceSettings ?? new DeviceSettings { Id = id, OriginalId = id }, details);
        }

        [HttpPut("device/{id}")]
        public async Task Set(string id, [FromBody] DeviceSettings value)
        {
            await _deviceSettingsStore.Set(id, value);
        }
    }

    public readonly record struct DeviceSettingsDetails(DeviceSettings? settings, IList<KeyValuePair<string, string>> details);
}
