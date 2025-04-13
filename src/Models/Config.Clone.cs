using Newtonsoft.Json;
using TextExtensions;
using YamlDotNet.Serialization;
using ESPresense.Extensions;
using System.Linq;

namespace ESPresense.Models
{
    public partial class Config
    {
        public Config Clone()
        {
            return new Config
            {
                Mqtt = Mqtt?.Clone(),
                Bounds = Bounds?.Select(b => b.ToArray()).ToArray(),
                Timeout = Timeout,
                AwayTimeout = AwayTimeout,
                Gps = Gps.Clone(),
                Map = Map.Clone(),
                Floors = Floors.Select(f => f.Clone()).ToArray(),
                Nodes = Nodes.Select(n => n.Clone()).ToArray(),
                Devices = Devices.Select(d => d.Clone()).ToArray(),
                ExcludeDevices = ExcludeDevices.Select(d => d.Clone()).ToArray(),
                History = History.Clone(),
                Locators = Locators.Clone(),
                Optimization = Optimization.Clone()
            };
        }
    }

    public partial class ConfigLocators
    {
        public ConfigLocators Clone()
        {
            return new ConfigLocators
            {
                NadarayaWatson = NadarayaWatson.Clone(),
                NealderMead = NealderMead.Clone(),
                NearestNode = NearestNode.Clone()
            };
        }
    }

    public partial class NadarayaWatsonConfig
    {
        public NadarayaWatsonConfig Clone()
        {
            return new NadarayaWatsonConfig
            {
                Enabled = Enabled,
                Floors = Floors?.ToArray(),
                Bandwidth = Bandwidth,
                Kernel = Kernel
            };
        }
    }

    public partial class NealderMeadConfig
    {
        public NealderMeadConfig Clone()
        {
            return new NealderMeadConfig
            {
                Enabled = Enabled,
                Floors = Floors?.ToArray(),
                Weighting = Weighting.Clone()
            };
        }
    }

    public partial class NearestNodeConfig
    {
        public NearestNodeConfig Clone()
        {
            return new NearestNodeConfig
            {
                Enabled = Enabled,
                MaxDistance = MaxDistance
            };
        }
    }

    public partial class ConfigMap
    {
        public ConfigMap Clone()
        {
            return new ConfigMap
            {
                FlipX = FlipX,
                FlipY = FlipY,
                WallThickness = WallThickness,
                WallColor = WallColor,
                WallOpacity = WallOpacity
            };
        }
    }

    public partial class ConfigOptimization
    {
        public ConfigOptimization Clone()
        {
            return new ConfigOptimization
            {
                Enabled = Enabled,
                IntervalSecs = IntervalSecs,
                KeepSnapshotMins = KeepSnapshotMins,
                Limits = new Dictionary<string, double>(Limits)
            };
        }
    }

    public partial class ConfigHistory
    {
        public ConfigHistory Clone()
        {
            return new ConfigHistory
            {
                Enabled = Enabled,
                Database = Database,
                ExpireAfter = ExpireAfter
            };
        }
    }

    public partial class ConfigWeighting
    {
        public ConfigWeighting Clone()
        {
            return new ConfigWeighting
            {
                Algorithm = Algorithm,
                Props = new Dictionary<string, double>(Props)
            };
        }
    }

    public partial class ConfigGps
    {
        public ConfigGps Clone()
        {
            return new ConfigGps
            {
                Latitude = Latitude,
                Longitude = Longitude,
                Elevation = Elevation
            };
        }
    }

    public partial class ConfigMqtt
    {
        public ConfigMqtt Clone()
        {
            return new ConfigMqtt
            {
                Host = Host,
                Port = Port,
                Ssl = Ssl,
                Username = Username,
                Password = Password,
                ClientId = ClientId
            };
        }
    }

    public partial class ConfigDevice
    {
        public ConfigDevice Clone()
        {
            return new ConfigDevice
            {
                Name = Name,
                Id = Id
            };
        }
    }

    public partial class ConfigFloor
    {
        public ConfigFloor Clone()
        {
            return new ConfigFloor
            {
                Id = Id,
                Name = Name,
                Bounds = Bounds?.Select(b => b.ToArray()).ToArray(),
                Rooms = Rooms?.Select(r => r.Clone()).ToArray()
            };
        }
    }

    public partial class ConfigRoom
    {
        public ConfigRoom Clone()
        {
            return new ConfigRoom
            {
                Id = Id,
                Name = Name,
                Points = Points?.Select(p => p.ToArray()).ToArray()
            };
        }
    }

    public partial class ConfigNode
    {
        public ConfigNode Clone()
        {
            return new ConfigNode
            {
                Name = Name,
                Id = Id,
                Point = Point?.ToArray(),
                Floors = Floors?.ToArray(),
                Enabled = Enabled,
                Stationary = Stationary
            };
        }
    }
}