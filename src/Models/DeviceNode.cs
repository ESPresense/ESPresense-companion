namespace ESPresense.Models;

public class DeviceNode
{
    public Device? Device { get; set; }
    public Node? Node { get; set; }

    public double Distance { get; set; }
    public double Rssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Node?.Config?.Timeout ?? 30);
    public double RefRssi { get; set; }

    public bool ReadMessage(DeviceMessage payload)
    {
        Rssi = payload.Rssi;
        RefRssi = payload.RefRssi;
        NewName(payload.Name);
        return NewDistance(payload.Distance);
    }

    private void NewName(string? name)
    {
        if (Device == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (Device.Name == name) return;
        Device.Name = name;
        Device.Check = true;
    }

    private bool NewDistance(double d)
    {
        var moved = Math.Abs(LastDistance - d) > 0.25;
        if (moved) LastDistance = d;
        Distance = d;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}