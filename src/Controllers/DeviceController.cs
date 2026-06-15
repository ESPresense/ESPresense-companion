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
        private readonly DeviceCaptureService _captureService;

        public DeviceController(ILogger<DeviceController> logger, DeviceSettingsStore deviceSettingsStore, DeviceService deviceService, State state, DeviceCaptureService captureService)
        {
            _logger = logger;
            _deviceSettingsStore = deviceSettingsStore;
            _deviceService = deviceService;
            _state = state;
            _captureService = captureService;
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
        public async Task<IActionResult> Set(string id, [FromBody] DeviceSettings value)
        {
            try
            {
                await _deviceSettingsStore.Set(id, value);
                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var deleted = await _deviceService.DeleteAsync(id, "manual");
            return deleted ? NoContent() : NotFound();
        }

        [HttpPost("{id}/capture/start")]
        public CaptureStatus StartCapture(string id)
        {
            var settings = _deviceSettingsStore.Get(id);
            return _captureService.Start(id, settings?.Id, settings?.OriginalId);
        }

        [HttpPost("{id}/capture/position")]
        public ActionResult<CaptureStatus> AddCapturePosition(string id, [FromBody] CapturePositionRequest position)
        {
            var status = _captureService.AddPosition(id, position.x, position.y, position.z, position.floor);
            return status == null ? NotFound() : status;
        }

        [HttpPost("{id}/capture/stop")]
        public ActionResult<CaptureStatus> StopCapture(string id)
        {
            var status = _captureService.Stop(id);
            return status == null ? NotFound() : status;
        }

        [HttpGet("{id}/capture")]
        public ActionResult<CaptureStatus> GetCapture(string id)
        {
            var status = _captureService.GetStatus(id);
            return status == null ? NotFound() : status;
        }

        [HttpDelete("{id}/capture")]
        public IActionResult DiscardCapture(string id)
        {
            return _captureService.Discard(id) ? NoContent() : NotFound();
        }

        [HttpGet("{id}/capture/export")]
        public IActionResult ExportCapture(string id)
        {
            var settings = _deviceSettingsStore.Get(id);
            var export = _captureService.Export(id, settings?.Name);
            if (export == null) return NotFound();
            var safeId = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{safeId}-capture-{export.Value.started:yyyyMMdd-HHmmss}.json";
            return File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export.Value), "application/json", fileName);
        }
    }

    public readonly record struct DeviceSettingsDetails(DeviceSettings? settings, IList<KeyValuePair<string, string>> details);

    public readonly record struct CapturePositionRequest(double x, double y, double z, string? floor);
}
