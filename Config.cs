using TextExtensions;
using YamlDotNet.Serialization;

namespace ESPresense
{
    
    public class Config
    {
        [YamlMember(Alias = "bounds")]
        public double[][] Bounds { get; set; }

        [YamlMember(Alias = "timeout")]
        public long Timeout { get; set; }

        [YamlMember(Alias = "away_timeout")]
        public long AwayTimeout { get; set; }

        [YamlMember(Alias = "floors")]
        public ConfigFloor[] Floors { get; set; }

        [YamlMember(Alias = "nodes")]
        public ConfigNode[] Nodes { get; set; }

        [YamlMember(Alias = "devices")]
        public ConfigDevice[] Devices { get; set; }
    }

    public class ConfigDevice
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        [YamlMember(Alias = "id")]
        public string Id { get; set; }
    }

    public class ConfigFloor
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        [YamlMember(Alias = "z")]
        public double Z { get; set; }

        [YamlMember(Alias = "rooms")]
        public ConfigRoom[] Rooms { get; set; }
    }

    public class ConfigRoom
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        [YamlMember(Alias = "points")]
        public double[][] Points { get; set; }
    }

    public class ConfigNode
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "point")]
        public double[] Point { get; set; }

        public string GetId() => Id ?? Name.ToSnakeCase().ToLower();
    }

}
