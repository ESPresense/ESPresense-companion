using System.Text.Json;

namespace ESPresense.Models;

public class DeviceNode
{
    public Device? Device { get; set; }
    public Node? Node { get; set; }

    public double Distance { get; set; }
    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double LastDistance { get; set; }

    public bool Current => DateTime.Now - LastHit < TimeSpan.FromSeconds(Node?.Config.Timeout ?? 30);

    public bool ReadMessage(byte[] payload)
    {
        var reader = new Utf8JsonReader(payload);
        string? prop = null;
        while (reader.Read())
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    break;
                case JsonTokenType.PropertyName:
                    prop = reader.GetString();
                    break;
                case JsonTokenType.Number:
                {
                    var intValue = reader.GetDouble();
                    switch (prop)
                    {
                        case "distance":
                            return NewDistance(intValue);
                    }

                    break;
                }

                default:
                    reader.Skip();
                    break;
            }

        return false;
    }

    private bool NewDistance(double d)
    {
        var moved = Math.Abs(LastDistance - d) > 0.5;
        if (moved) LastDistance = d;
        Distance = d;
        LastHit = DateTime.UtcNow;
        Hits++;
        return moved;
    }
}