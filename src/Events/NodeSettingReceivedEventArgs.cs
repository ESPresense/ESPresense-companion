namespace ESPresense.Events;

public class NodeSettingReceivedEventArgs
{
    public string? NodeId { get; set; }
    public string? Setting { get; set; }
    public string? Payload { get; set; }
}