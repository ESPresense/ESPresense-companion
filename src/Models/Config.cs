using Newtonsoft.Json;
using TextExtensions;
using YamlDotNet.Serialization;
using ESPresense.Extensions;

namespace ESPresense.Models
{
    public class Config
    {
        [YamlMember(Alias = "mqtt")]
        public ConfigMqtt? Mqtt { get; set; }

        [YamlMember(Alias = "bounds")]
        public double[][]? Bounds { get; set; }

        [YamlMember(Alias = "timeout")]
        public int Timeout { get; set; } = 30;

        [YamlMember(Alias = "away_timeout")]
        public int AwayTimeout { get; set; } = 120;

        [YamlMember(Alias = "gps")]
        public ConfigGps Gps { get; set; } = new();

        [YamlMember(Alias = "map")]
        public ConfigMap Map { get; set; } = new();

        [YamlMember(Alias = "floors")]
        public ConfigFloor[] Floors { get; set; } = Array.Empty<ConfigFloor>();

        [YamlMember(Alias = "nodes")]
        public ConfigNode[] Nodes { get; set; } = Array.Empty<ConfigNode>();

        [YamlMember(Alias = "devices")]
        public ConfigDevice[] Devices { get; set; } = Array.Empty<ConfigDevice>();

        [YamlMember(Alias = "exclude_devices")]
        public ConfigDevice[] ExcludeDevices { get; set; } = Array.Empty<ConfigDevice>();

        [YamlMember(Alias = "history")]
        public ConfigHistory History { get; set; } = new();

        [YamlMember(Alias = "locators")]
        public ConfigLocators Locators { get; set; } = new();

        [YamlMember(Alias = "optimization")]
        public ConfigOptimization Optimization { get; set; } = new();
    }

    public class ConfigLocators
    {
        [YamlMember(Alias = "nadaraya_watson")]
        public NadarayaWatsonConfig NadarayaWatson { get; set; } = new();

        [YamlMember(Alias = "nealder_mead")]
        public NealderMeadConfig NealderMead { get; set; } = new();

        [YamlMember(Alias = "nearest_node")]
        public NearestNodeConfig NearestNode { get; set; } = new();
    }

    public class NadarayaWatsonConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "bandwidth")]
        public double Bandwidth { get; set; } = 0.5;

        [YamlMember(Alias = "kernel")]
        public string Kernel { get; set; } = "gaussian";
    }

    public class NealderMeadConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public class NearestNodeConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "max_distance")]
        public double? MaxDistance { get; set; }
    }

    public class ConfigMap
    {
        [YamlMember(Alias = "flip_x")]
        public bool FlipX { get; set; } = false;

        [YamlMember(Alias = "flip_y")]
        public bool FlipY { get; set; } = true;

        [YamlMember(Alias = "wall_thickness")]
        public double WallThickness { get; set; } = 0.1;  // Default wall thickness in meters

        [YamlMember(Alias = "wall_color")]
        public string? WallColor { get; set; }  // Optional wall color, defaults to room color if not set

        [YamlMember(Alias = "wall_opacity")]
        public double? WallOpacity { get; set; }  // Optional wall opacity, defaults to 0.35 if not set
    }

    public class ConfigOptimization
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;
        [YamlMember(Alias = "interval_secs")] public int IntervalSecs { get; set; } = 60;
        [YamlMember(Alias = "max_snapshots")] public int MaxSnapshots { get; set; } = 60;

        [YamlMember(Alias = "limits")] public Dictionary<string, double> Limits { get; set;  } = new();

        [YamlIgnore] public double AbsorptionMin => Limits.TryGetValue("absorption_min", out var val) ? val : 2;
        [YamlIgnore] public double AbsorptionMax => Limits.TryGetValue("absorption_max", out var val) ? val : 4;
        [YamlIgnore] public double TxRefRssiMin => Limits.TryGetValue("tx_ref_rssi_min", out var val) ? val : -70;
        [YamlIgnore] public double TxRefRssiMax => Limits.TryGetValue("tx_ref_rssi_max", out var val) ? val : -50;
        [YamlIgnore] public double RxAdjRssiMin => Limits.TryGetValue("rx_adj_rssi_min", out var val) ? val : -20;
        [YamlIgnore] public double RxAdjRssiMax => Limits.TryGetValue("rx_adj_rssi_max", out var val) ? val : 20;
    }

    public class ConfigHistory
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;

        [YamlMember(Alias = "db")]
        public string Database { get; set; } = "sqlite:///espresense.db";

        [YamlMember(Alias = "expire_after")] public string ExpireAfter { get; set; } = "24h";

        [YamlIgnore]
        public TimeSpan ExpireAfterTimeSpan => ExpireAfter.TryParseDurationString(out var ts) ? ts : TimeSpan.FromHours(24);
    }

    public class ConfigWeighting
    {
        [YamlMember(Alias = "algorithm")]
        public string Algorithm { get; set; } = "gaussian";

        [YamlMember(Alias = "props")] public Dictionary<string, double> Props { get; set; } = new();
    }

    public class ConfigGps
    {
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Elevation { get; set; }
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

        [JsonProperty("client_id")]
        [YamlMember(Alias = "client_id")]
        public string ClientId { get; set; } = "espresense-companion";
    }

    public class ConfigDevice
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public class ConfigFloor
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "bounds")]
        public double[][]? Bounds { get; set; }

        [YamlMember(Alias = "rooms")]
        public ConfigRoom[]? Rooms { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public class ConfigRoom
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "points")]
        public double[][]? Points { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public class ConfigNode
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "point")]
        public double[]? Point { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = true;

        [YamlMember(Alias = "stationary")]
        public bool Stationary { get; set; } = true;

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }
}
