namespace ESPresense.Models;

public class Calibration
{
    public Dictionary<string, Dictionary<string, Dictionary<string, double>>> Matrix { get; } = new();
}