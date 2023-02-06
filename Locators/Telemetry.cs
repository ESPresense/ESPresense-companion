using ConcurrentCollections;

namespace ESPresense.Locators
{
    public class Telemetry
    {
        public int Messages { get; internal set; }
        public int Devices { get; internal set; }
        public int Tracked { get; internal set; }
        public int Skipped { get; internal set; }
        public int Malformed { get; internal set; }

        public ConcurrentHashSet<string> UnknownNodes = new();
    }
}