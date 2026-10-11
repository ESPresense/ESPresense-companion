namespace ESPresense.Events;

public class NodeUpdateErrorEventArgs
{
    public string NodeId { get; set; }

    /// <summary>The node's last update failure, or null when it was cleared.</summary>
    public string? Error { get; set; }
}
