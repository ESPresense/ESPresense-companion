using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace ESPresense.Models;

public class NodeSettings(string id)
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    [StringLength(64)]
    public string? Id { get; set; } = id;

    public UpdatingSettings Updating { get; set; } = new UpdatingSettings();
    public ScanningSettings Scanning { get; set; } = new ScanningSettings();
    public CountingSettings Counting { get; set; } = new CountingSettings();
    public FilteringSettings Filtering { get; set; } = new FilteringSettings();
    public CalibrationSettings Calibration { get; set; } = new CalibrationSettings();

    public NodeSettings Clone()
    {
        return new NodeSettings(id)
        {
            Updating = Updating.Clone(),
            Scanning = Scanning.Clone(),
            Counting = Counting.Clone(),
            Filtering = Filtering.Clone(),
            Calibration = Calibration.Clone()
        };
    }
}

public class UpdatingSettings
{
    public bool? AutoUpdate { get; set; }
    public bool? PreRelease { get; set; }
    public UpdatingSettings Clone() => (UpdatingSettings)MemberwiseClone();
}

public class ScanningSettings
{
    public int? ForgetAfterMs { get; set; }
    public ScanningSettings Clone() => (ScanningSettings)MemberwiseClone();
}

public class CountingSettings
{
    public string? IdPrefixes { get; set; }
    public double? StartCountingDistance { get; set; }
    public double? StopCountingDistance { get; set; }
    public int? IncludeDevicesAge { get; set; }
    public CountingSettings Clone() => (CountingSettings)MemberwiseClone();
}

public class FilteringSettings
{
    public string? IncludeIds { get; set; }
    public string? ExcludeIds { get; set; }
    public double? MaxDistance { get; set; }
    public double? EarlyReportDistance { get; set; }
    public int? SkipReportAge { get; set; }
    public FilteringSettings Clone() => (FilteringSettings)MemberwiseClone();
}

public class CalibrationSettings
{
    public int? RssiAt1m { get; set; }
    public int? RxAdjRssi { get; set; }
    public double? Absorption { get; set; }
    public int? TxRefRssi { get; set; }
    public CalibrationSettings Clone() => (CalibrationSettings)MemberwiseClone();
}