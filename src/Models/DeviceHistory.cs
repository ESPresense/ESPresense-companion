namespace ESPresense.Models;

public class DeviceHistory
{
    public string? Id { get; set; }
    public string? Scenario { get; set; }
    public int Confidence { get; set; }
    public int Fixes { get; set; }
    public DateTime? When { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Z { get; set; }
    public bool Best { get; set; }
}