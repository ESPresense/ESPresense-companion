using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Text.Json;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private const string SettingsFilePath = "settings.json";

    [HttpGet]
    public ActionResult<Settings> GetSettings()
    {
        if (!System.IO.File.Exists(SettingsFilePath))
        {
            return new Settings();
        }

        var json = System.IO.File.ReadAllText(SettingsFilePath);
        return JsonSerializer.Deserialize<Settings>(json);
    }

    [HttpPost]
    public IActionResult SaveSettings([FromBody] Settings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        System.IO.File.WriteAllText(SettingsFilePath, json);
        return Ok();
    }
}

public class Settings
{
    public UpdatingSettings Updating { get; set; } = new UpdatingSettings();
    public ScanningSettings Scanning { get; set; } = new ScanningSettings();
    public CountingSettings Counting { get; set; } = new CountingSettings();
    public FilteringSettings Filtering { get; set; } = new FilteringSettings();
    public CalibrationSettings Calibration { get; set; } = new CalibrationSettings();
}

public class UpdatingSettings
{
    public bool? AutoUpdate { get; set; }
    public bool? PreRelease { get; set; }
}

public class ScanningSettings
{
    public int? ForgetAfterMs { get; set; }
}

public class CountingSettings
{
    public string? IdPrefixes { get; set; }
    public double? StartCountingDistance { get; set; }
    public double? StopCountingDistance { get; set; }
    public int? IncludeDevicesAge { get; set; }
}

public class FilteringSettings
{
    public string? IncludeIds { get; set; }
    public string? ExcludeIds { get; set; }
    public double? MaxReportDistance { get; set; }
    public double? EarlyReportDistance { get; set; }
    public int? SkipReportAge { get; set; }
}

public class CalibrationSettings
{
    public int? RssiAt1m { get; set; }
    public int? RssiAdjustment { get; set; }
    public double? AbsorptionFactor { get; set; }
    public int? IBeaconRssiAt1m { get; set; }
}