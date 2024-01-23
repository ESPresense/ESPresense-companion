using ConcurrentCollections;

namespace ESPresense.Locators
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

        public readonly ConcurrentHashSet<string> UnknownNodes = new();

        public Telemetry Clone()
        {
            return (Telemetry)MemberwiseClone();
        }
    }
}