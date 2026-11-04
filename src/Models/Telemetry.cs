using System.Text.Json.Serialization;
using ConcurrentCollections;

namespace ESPresense.Models
{
    public class Telemetry
    {
        public string? Ip { get; set; }
        public int Messages { get; internal set; }
        public int Devices { get; internal set; }
        public int Tracked { get; internal set; }
        public int Skipped { get; internal set; }
        public int Malformed { get; internal set; }
        public int Moved { get; internal set; }

        // A field: only the MQTT telemetry payload (which includes fields) carries it, first, as it always has.
        [JsonPropertyOrder(-1)]
        public readonly ConcurrentHashSet<string> UnknownNodes = new();

        public Telemetry Clone()
        {
            return (Telemetry)MemberwiseClone();
        }
    }
}