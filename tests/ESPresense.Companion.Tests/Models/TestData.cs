using ESPresense.Models;

public class TestData
{
    public string tst { get; set; }
    public string topic { get; set; }
    public int qos { get; set; }
    public int retain { get; set; }
    public int payloadlen { get; set; }
    public DeviceMessage payload { get; set; }
}