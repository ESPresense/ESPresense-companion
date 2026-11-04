using ESPresense.Models;
using ESPresense.Services;
using Microsoft.AspNetCore.Mvc;

namespace ESPresense.Controllers;

[ApiController]
[Route("api/device/{id}/fingerprint")]
public class FingerprintController(State state, DeviceSettingsStore settings, FingerprintCollectionService collection, NodeSettingsStore nodeSettings) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Collect(string id, [FromBody] CapturePositionRequest position, CancellationToken cancellationToken)
    {
        if (position.floor == null || !state.Floors.TryGetValue(position.floor, out var floor) ||
            !double.IsFinite(position.x) || !double.IsFinite(position.y) || !double.IsFinite(position.z))
            return BadRequest(new { error = "Choose a valid floor and position on the map." });
        if (floor.Bounds is { Length: >= 2 } bounds &&
            (position.x < bounds[0].X || position.x > bounds[1].X || position.y < bounds[0].Y || position.y > bounds[1].Y ||
             position.z < bounds[0].Z || position.z > bounds[1].Z))
            return BadRequest(new { error = "The position must be inside the selected floor's bounds." });

        var config = state.Config?.Locators?.Fingerprint ?? new FingerprintConfig();
        var deviceSettings = settings.Get(id);
        var ids = new[] { id, deviceSettings?.Id, deviceSettings?.OriginalId }.OfType<string>();
        // Freeze geometry and calibration settings at the confirmed position before collecting.
        var point = new MathNet.Spatial.Euclidean.Point3D(position.x, position.y, position.z);
        var references = state.Nodes.Values
            .Where(n => n.HasLocation && n.Floors?.Any(f => f.Id == floor.Id) == true)
            .ToDictionary(n => n.Id, n => new CalibrationReference(n.Location.DistanceTo(point),
                nodeSettings.Get(n.Id).Calibration.Absorption ?? 2), StringComparer.OrdinalIgnoreCase);
        var readings = await collection.CollectAsync(ids, cancellationToken);
        var rssi = readings.ToDictionary(p => p.Key, p => p.Value.RawRssi, StringComparer.OrdinalIgnoreCase);
        if (rssi.Count < Math.Max(2, config.MinCommon))
            return BadRequest(new { error = $"Heard {rssi.Count} nodes; at least {Math.Max(2, config.MinCommon)} are needed. Keep the device nearby and try again." });

        cancellationToken.ThrowIfCancellationRequested();
        var sampleId = Guid.NewGuid();
        try
        {
            collection.Save(new Fingerprint { Id = sampleId, DeviceId = deviceSettings?.OriginalId ?? id, Floor = floor.Id, X = position.x, Y = position.y, Z = position.z, Rssi = rssi }, config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Problem("The fingerprint file could not be updated. Check that it is valid and writable.");
        }
        return Ok(new { sampleId, nodes = rssi.Count, enabled = config.Enabled, calibration = FingerprintCollectionService.CalculateCalibration(readings, references) });
    }

    [HttpDelete("{sampleId:guid}")]
    public IActionResult Remove(string id, Guid sampleId)
    {
        var config = state.Config?.Locators?.Fingerprint ?? new FingerprintConfig();
        var owner = settings.Get(id)?.OriginalId ?? id;
        try
        {
            return collection.Remove(sampleId, owner, config)
                ? NoContent()
                : NotFound(new { error = "This sample was not found in the current fingerprint file." });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Problem("The fingerprint file could not be updated. Check that it is valid and writable.");
        }
    }

}
