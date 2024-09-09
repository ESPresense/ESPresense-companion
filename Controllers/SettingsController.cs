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
    public Updating Updating { get; set; }
    public Scanning Scanning { get; set; }
    public Counting Counting { get; set; }
    public Filtering Filtering { get; set; }
    public Calibration Calibration { get; set; }
}

public class Updating
{
    public bool AutoUpdate { get; set; }
    public bool PreRelease { get; set; }
}

public class Scanning
{
    public int? ForgetAfterMs { get; set; }
}

public class Counting
{
    public string IdPrefixes { get; set; }
    public double? StartCountingDistance { get; set; }
    public double? StopCountingDistance { get; set; }
    public int? IncludeDevicesAge { get; set; }
}

public class Filtering
{
    public string IncludeIds { get; set; }
    public string ExcludeIds { get; set; }
    public double? MaxReportDistance { get; set; }
    public double? EarlyReportDistance { get; set; }
    public int? SkipReportAge { get; set; }
}

public class Calibration
{
    public int? RssiAt1m { get; set; }
    public int? RssiAdjustment { get; set; }
    public double? AbsorptionFactor { get; set; }
    public int? IBeaconRssiAt1m { get; set; }
}