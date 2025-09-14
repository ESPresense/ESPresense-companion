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
        private readonly DeviceService _deviceService;
        private readonly State _state;

        public DeviceController(ILogger<DeviceController> logger, DeviceSettingsStore deviceSettingsStore, DeviceService deviceService, State state)
        {
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
            _deviceService = deviceService;
            _state = state;
        }

        [HttpGet("{id}")]
        public DeviceSettingsDetails Get(string id)
        {
            var deviceSettings = _deviceSettingsStore.Get(id);
            var details = new List<KeyValuePair<string, string>>();
            if (deviceSettings?.Id != null && _state.Devices.TryGetValue(deviceSettings.Id, out var device))
                details.AddRange(device.GetDetails());
            return new DeviceSettingsDetails(deviceSettings ?? new DeviceSettings { Id = id, OriginalId = id }, details);
        }

        [HttpPut("{id}")]
        public async Task Set(string id, [FromBody] DeviceSettings value)
        {
            await _deviceSettingsStore.Set(id, value);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var deleted = await _deviceService.DeleteAsync(id, "manual");
            return deleted ? NoContent() : NotFound();
        }
    }

    public readonly record struct DeviceSettingsDetails(DeviceSettings? settings, IList<KeyValuePair<string, string>> details);
}
