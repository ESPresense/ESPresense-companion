namespace ESPresense.Events;

public class NodeStatusReceivedEventArgs
{
    public string? NodeId { get; set; }
    public bool Online { get; set; }
}