using Newtonsoft.Json;
using TextExtensions;
using YamlDotNet.Serialization;

namespace ESPresense.Models
{
    public class Config
    {
        [JsonProperty("mqtt")]
        public ConfigMqtt? Mqtt { get; set; }

        [YamlMember(Alias = "bounds")]
        public double[][]? Bounds { get; set; }

        [YamlMember(Alias = "timeout")]
        public long Timeout { get; set; } = 30;

        [YamlMember(Alias = "away_timeout")]
        public long AwayTimeout { get; set; } = 120;

        [YamlMember(Alias = "floors")]
        public ConfigFloor[]? Floors { get; set; }

        [YamlMember(Alias = "nodes")]
        public ConfigNode[]? Nodes { get; set; }

        [YamlMember(Alias = "devices")]
        public ConfigDevice[]? Devices { get; set; }
    }

    public partial class ConfigMqtt
    {
        [JsonProperty("host")]
        public string? Host { get; set; }

        [JsonProperty("port")]
        public int? Port { get; set; }

        [JsonProperty("ssl")]
        public bool? Ssl { get; set; }

        [JsonProperty("username")]
        public string? Username { get; set; }

        [JsonProperty("password")]
        public string? Password { get; set; }
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

        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = true;

        public string GetId() => Id ?? Name.ToSnakeCase().ToLower();
    }

}
