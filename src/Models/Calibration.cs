namespace ESPresense.Models;

public class Calibration
{
    public double? RMSE { get; set; }
    public double? R { get; set; }
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Matrix { get; } = new();
    public Dictionary<string, string> Antennas { get; } = new();
    public OptimizerState OptimizerState { get; set; } = new();
    public HashSet<string> Anchored { get; } = new();
}
