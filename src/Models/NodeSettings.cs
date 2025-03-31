﻿using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace ESPresense.Models;

public class NodeSettings(string? id = null, string? name = null)
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    [StringLength(64)]
    public string? Id
    {
        get => id;
        set => id = value;
    }

    [JsonPropertyName("name")]
    [JsonProperty("name")]
    [StringLength(64)]
    public string? Name
    {
        get => name;
        set => name = value;
    }

    [JsonPropertyName("updating")]
    [JsonProperty("updating")]
    public UpdatingSettings Updating { get; set; } = new UpdatingSettings();

    [JsonPropertyName("scanning")]
    [JsonProperty("scanning")]
    public ScanningSettings Scanning { get; set; } = new ScanningSettings();

    [JsonPropertyName("counting")]
    [JsonProperty("counting")]
    public CountingSettings Counting { get; set; } = new CountingSettings();

    [JsonPropertyName("filtering")]
    [JsonProperty("filtering")]
    public FilteringSettings Filtering { get; set; } = new FilteringSettings();

    [JsonPropertyName("calibration")]
    [JsonProperty("calibration")]
    public CalibrationSettings Calibration { get; set; } = new CalibrationSettings();

    public NodeSettings Clone()
    {
        return new NodeSettings(id, name)
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
    public bool? Prerelease { get; set; }
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
    public double? MinDistance { get; set; }
    public double? MaxDistance { get; set; }
    public int? MinMs { get; set; }
    public CountingSettings Clone() => (CountingSettings)MemberwiseClone();
}

public class FilteringSettings
{
    public string? IncludeIds { get; set; }
    public string? ExcludeIds { get; set; }
    public double? MaxDistance { get; set; }
    public double? SkipDistance { get; set; }
    public int? SkipMs { get; set; }
    public FilteringSettings Clone() => (FilteringSettings)MemberwiseClone();
}

public class CalibrationSettings
{
    public int? RxRefRssi { get; set; }
    public int? RxAdjRssi { get; set; }
    public double? Absorption { get; set; }
    public int? TxRefRssi { get; set; }
    public CalibrationSettings Clone() => (CalibrationSettings)MemberwiseClone();
}