using System.Text.Json;
using Serilog;

namespace ESPresense.Models;

public class RxNode
{
    public Node? Tx { get; set; }
    public Node? Rx { get; set; }

    public double Distance { get; set; }
    public double Rssi { get; set; }

    public DateTime? LastHit { get; set; }
    public int Hits { get; set; }

    public double MapDistance => Tx?.Location.DistanceTo(Rx!.Location) ?? -1;

    public double LastDistance { get; set; }

    public bool Current => DateTime.UtcNow - LastHit < TimeSpan.FromSeconds(Tx?.Config?.Timeout ?? 30);
    public double RefRssi { get; set; }

    public bool ReadMessage(byte[] payload)
    {
        bool moved = false;

        try
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
                        switch (prop)
                        {
                            case "distance":
                                moved |= NewDistance(reader.GetDouble());
                                break;
                            case "rssi":
                                Rssi = reader.GetDouble();
                                break;
                            case "rssi@1m":
                                RefRssi = reader.GetDouble();
                                break;
                        }

                        break;
                    default:
                        reader.Skip();
                        break;
                }

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error reading mqtt message");
        }
        return moved;
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