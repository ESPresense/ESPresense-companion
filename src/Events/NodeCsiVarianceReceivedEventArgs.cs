namespace ESPresense.Events;

public class NodeCsiVarianceReceivedEventArgs
{
    public string NodeId { get; set; } = "";
    public double Value { get; set; }
}
