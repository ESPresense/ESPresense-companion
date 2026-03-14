namespace ESPresense.Models;

public class Calibration
{
    public double? RMSE { get; set; }
    public double? R { get; set; }
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Matrix { get; } = new();
    public Dictionary<string, NodeCalibrationSummary> Nodes { get; } = new();
    public OptimizerState OptimizerState { get; set; } = new();
    public HashSet<string> Anchored { get; } = new();
}

public class NodeCalibrationSummary
{
    public string? Antenna { get; set; }
    public double? Azimuth { get; set; }
    public double? Elevation { get; set; }
    public double? Absorption { get; set; }
    public double? RxAdjRssi { get; set; }
}
