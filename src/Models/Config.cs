using TextExtensions;
using YamlDotNet.Serialization;
using ESPresense.Extensions;

namespace ESPresense.Models
{
    public partial class Config
    {
        [YamlMember(Alias = "mqtt")]
        public ConfigMqtt Mqtt { get; set; } = new();

        [YamlMember(Alias = "gps")]
        public ConfigGps Gps { get; set; } = new();

        [YamlMember(Alias = "map")]
        public ConfigMap Map { get; set; } = new();

        [YamlMember(Alias = "timeout")]
        public int Timeout { get; set; } = 30;

        [YamlMember(Alias = "away_timeout")]
        public int AwayTimeout { get; set; } = 120;

        // Retention policy for inactive devices (duration string, e.g., "30d", "720h")
        [YamlMember(Alias = "device_retention")]
        public string DeviceRetention { get; set; } = "30d";

        [YamlIgnore]
        public TimeSpan DeviceRetentionTimeSpan => DeviceRetention.TryParseDurationString(out var ts, DurationUnit.Days) ? ts : TimeSpan.FromDays(30);

        [YamlMember(Alias = "optimization")]
        public ConfigOptimization Optimization { get; set; } = new();

        [YamlMember(Alias = "room_learning")]
        public RoomLearningConfig RoomLearning { get; set; } = new();

        [YamlMember(Alias = "locators")]
        public ConfigLocators Locators { get; set; } = new();

        [YamlMember(Alias = "survey")]
        public ConfigSurvey Survey { get; set; } = new();

        [YamlMember(Alias = "accuracy")]
        public ConfigAccuracy Accuracy { get; set; } = new();

        [YamlMember(Alias = "filtering")]
        public ConfigFiltering Filtering { get; set; } = new();

        [YamlMember(Alias = "history")]
        public ConfigHistory History { get; set; } = new();

        [YamlMember(Alias = "floors")]
        public ConfigFloor[] Floors { get; set; } = Array.Empty<ConfigFloor>();

        [YamlMember(Alias = "nodes")]
        public ConfigNode[] Nodes { get; set; } = Array.Empty<ConfigNode>();

        [YamlMember(Alias = "devices")]
        public ConfigDevice[] Devices { get; set; } = Array.Empty<ConfigDevice>();

        [YamlMember(Alias = "exclude_devices")]
        public ConfigDevice[] ExcludeDevices { get; set; } = Array.Empty<ConfigDevice>();
    }

    public class RoomLearningConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }
    }

    /// <summary>Automatic fingerprint surveying; see RobotSurveyService.</summary>
    public partial class ConfigSurvey
    {
        [YamlMember(Alias = "robot")]
        public RobotSurveyConfig Robot { get; set; } = new();
    }

    /// <summary>
    /// A beacon riding on a robot that knows where it is (a LiDAR vacuum). Every pose the robot reports
    /// while the beacon is heard becomes survey data: a complete fingerprint where it paused, a sparse
    /// time bin where it drove in a straight line.
    /// </summary>
    public partial class RobotSurveyConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        /// <summary>The beacon as Companion knows it, e.g. "node:calibrate" for an ESPresense node used as a beacon.</summary>
        [YamlMember(Alias = "device")]
        public string? Device { get; set; }

        /// <summary>Floor the robot surveys.</summary>
        [YamlMember(Alias = "floor")]
        public string? Floor { get; set; }

        /// <summary>Beacon height above the floor (m).</summary>
        [YamlMember(Alias = "height")]
        public double Height { get; set; } = 0.15;

        [YamlMember(Alias = "pose")]
        public RobotPoseConfig Pose { get; set; } = new();

        [YamlMember(Alias = "transform")]
        public RobotTransformConfig Transform { get; set; } = new();

        /// <summary>Poses within this distance (m) over <see cref="DwellSecs"/> count as standing still.</summary>
        [YamlMember(Alias = "stationary_m")]
        public double StationaryM { get; set; } = 0.2;

        /// <summary>How long (s) the robot must stand still for a complete fingerprint, and how often a long pause yields another.</summary>
        [YamlMember(Alias = "dwell_secs")]
        public double DwellSecs { get; set; } = 6;

        /// <summary>After standing still this long (s) the robot is parked, and nothing more is recorded until it moves.</summary>
        [YamlMember(Alias = "max_dwell_secs")]
        public double MaxDwellSecs { get; set; } = 90;

        /// <summary>Also record one-second bins while the robot drives a straight line, as sparse fingerprints.</summary>
        [YamlMember(Alias = "moving")]
        public bool Moving { get; set; } = true;

        /// <summary>Fastest (m/s) the robot may be moving for a bin to be placed by interpolating its poses.</summary>
        [YamlMember(Alias = "max_speed")]
        public double MaxSpeed { get; set; } = 1.0;

        /// <summary>Oldest robot samples are dropped past this many fingerprints; taught samples are never dropped.</summary>
        [YamlMember(Alias = "max_fingerprints")]
        public int MaxFingerprints { get; set; } = 20000;
    }

    /// <summary>Where the robot's pose comes from: a Home Assistant entity attribute (polled), or POST /api/survey/pose.</summary>
    public partial class RobotPoseConfig
    {
        /// <summary>Entity whose attributes carry the pose, e.g. the vacuum's map camera.</summary>
        [YamlMember(Alias = "entity")]
        public string? Entity { get; set; }

        /// <summary>Attribute holding {x, y, a}; a is the heading in degrees when present.</summary>
        [YamlMember(Alias = "attribute")]
        public string Attribute { get; set; } = "vacuum_position";

        /// <summary>Attribute naming the robot's map; when it changes from what was first seen, surveying pauses: the poses are in another frame.</summary>
        [YamlMember(Alias = "map_attribute")]
        public string? MapAttribute { get; set; }

        [YamlMember(Alias = "poll_secs")]
        public double PollSecs { get; set; } = 2;
    }

    /// <summary>
    /// Robot frame to floorplan metres: floor = rotate(scale * (x, flip_y ? -y : y), rotation_deg) + offset.
    /// Fit it from geometry (park under two nodes you can see), never from the radio.
    /// </summary>
    public partial class RobotTransformConfig
    {
        /// <summary>Metres per robot unit (0.001 for millimetres).</summary>
        [YamlMember(Alias = "scale")]
        public double Scale { get; set; } = 0.001;

        [YamlMember(Alias = "rotation_deg")]
        public double RotationDeg { get; set; }

        [YamlMember(Alias = "offset")]
        public double[] Offset { get; set; } = { 0, 0 };

        [YamlMember(Alias = "flip_y")]
        public bool FlipY { get; set; }
    }

    /// <summary>The ruler: held-out accuracy of the fingerprint locator, published as sensors; see AccuracyService.</summary>
    public partial class ConfigAccuracy
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>How often (s) anchored devices are re-located as if unknown and compared to their anchors.</summary>
        [YamlMember(Alias = "interval_secs")]
        public int IntervalSecs { get; set; } = 60;

        /// <summary>How often (min), at most, the survey is scored by leaving spots out; also only when it changed.</summary>
        [YamlMember(Alias = "holdout_interval_mins")]
        public int HoldoutIntervalMins { get; set; } = 15;

        /// <summary>How many surveyed spots are held out and located from the rest.</summary>
        [YamlMember(Alias = "holdout_spots")]
        public int HoldoutSpots { get; set; } = 40;
    }

    public partial class ConfigLocators
    {
        [YamlMember(Alias = "nadaraya_watson")]
        public NadarayaWatsonConfig NadarayaWatson { get; set; } = new();

        [YamlMember(Alias = "nelder_mead")]
        public NelderMeadConfig NelderMead { get; set; } = new();

        [YamlMember(Alias = "bfgs")]
        public BfgsConfig Bfgs { get; set; } = new();

        [YamlMember(Alias = "mle")]
        public MleConfig Mle { get; set; } = new();

        [YamlMember(Alias = "multi_floor")]
        public MultiFloorConfig MultiFloor { get; set; } = new();

        [YamlMember(Alias = "nearest_node")]
        public NearestNodeConfig NearestNode { get; set; } = new();

        [YamlMember(Alias = "fingerprint")]
        public FingerprintConfig Fingerprint { get; set; } = new();
    }

    public partial class FingerprintConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        /// <summary>Fingerprint survey file, relative to the config directory.</summary>
        [YamlMember(Alias = "file")]
        public string File { get; set; } = "fingerprints.json";

        /// <summary>
        /// "continuous" (default) matches against the survey smoothed over position, so devices between
        /// surveyed spots are located between them; "knn" averages the k closest fingerprints.
        /// </summary>
        [YamlMember(Alias = "mode")]
        public string Mode { get; set; } = "continuous";

        /// <summary>Smoothing radius (m) of the continuous surface; about 0.8x the survey spacing.</summary>
        [YamlMember(Alias = "bandwidth")]
        public double Bandwidth { get; set; } = 0.6;

        /// <summary>How many best-matching surface points to average (continuous mode).</summary>
        [YamlMember(Alias = "top")]
        public int Top { get; set; } = 20;

        /// <summary>How many closest fingerprints to average (knn mode).</summary>
        [YamlMember(Alias = "k")]
        public int K { get; set; } = 3;

        /// <summary>Fewest nodes a reading and a fingerprint must share to be compared.</summary>
        [YamlMember(Alias = "min_common")]
        public int MinCommon { get; set; } = 3;

        /// <summary>
        /// RSSI assumed for a node heard on only one side of a comparison (roughly the hearing limit);
        /// null compares shared nodes only (knn mode only; continuous mode treats null as -100). On the robot survey -100 cut the worst errors sharply.
        /// </summary>
        [YamlMember(Alias = "missing_rssi")]
        public double? MissingRssi { get; set; } = -100;

        /// <summary>
        /// Seconds of reports averaged per node before matching (0 = latest report only). The survey averaged
        /// a dwell of tens of seconds; a device that has stayed put this long matches it the same way.
        /// </summary>
        [YamlMember(Alias = "reading_window_secs")]
        public double ReadingWindowSecs { get; set; } = 15;

        /// <summary>Smallest per-node spread (dB) the surface will claim; single reports carry about this much fading noise.</summary>
        [YamlMember(Alias = "sigma_floor")]
        public double SigmaFloor { get; set; } = 2.0;

        /// <summary>
        /// Regress each placed node's rssi as a residual over a log-distance prior from its position. On the robot
        /// survey this trimmed the worst errors (p90 1.95 to 1.76 m) and cost the median 0.05 m (0.69 to 0.74), so it is off.
        /// </summary>
        [YamlMember(Alias = "prior")]
        public bool Prior { get; set; }

        /// <summary>
        /// When a fingerprint fix is current with at least <see cref="MinConfidence"/>, it is reported and the
        /// ranging locators only stand in where the survey has no support.
        /// </summary>
        [YamlMember(Alias = "priority")]
        public bool Priority { get; set; } = true;

        [YamlMember(Alias = "min_confidence")]
        public int MinConfidence { get; set; } = 20;

        [YamlMember(Alias = "tracker")]
        public FingerprintTrackerConfig Tracker { get; set; } = new();
    }

    /// <summary>
    /// The filter that carries a fingerprint fix from report to report; see FingerprintTracker. Off by default:
    /// on the robot survey it locates no better than the plain locator, which is already smoothed; it gives a
    /// fix that does not jump and a confidence from the spread of its belief.
    /// </summary>
    public partial class FingerprintTrackerConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        /// <summary>How far (m) the belief spreads per second without evidence; about a walking pace's uncertainty.</summary>
        [YamlMember(Alias = "speed")]
        public double Speed { get; set; } = 0.7;

        /// <summary>How much of an independent sample each report is worth (fading on nearby reports is correlated).</summary>
        [YamlMember(Alias = "temper")]
        public double Temper { get; set; } = 0.5;

        /// <summary>Weight of a known node's silence relative to a heard node's report.</summary>
        [YamlMember(Alias = "silent_weight")]
        public double SilentWeight { get; set; } = 1.0;
    }

    public partial class NadarayaWatsonConfig
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

    public partial class NelderMeadConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class BfgsConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class MleConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class MultiFloorConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        [YamlMember(Alias = "weighting")]
        public ConfigWeighting Weighting { get; set; } = new();
    }

    public partial class NearestNodeConfig
    {
        [YamlMember(Alias = "enabled")]
        public bool Enabled { get; set; }

        // Don't fall back to a node whose distance to the device is above this (metres). Unset or <= 0: no limit.
        [YamlMember(Alias = "max_distance")]
        public double? MaxDistance { get; set; }
    }

    public partial class ConfigMap
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

    public partial class ConfigOptimization
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;
        [YamlMember(Alias = "optimizer")] public string Optimizer { get; set; } = "per_node_absorption"; // per_node_absorption (default) or device_residual; the removed legacy and global_absorption modes fall back to per_node_absorption
        [YamlMember(Alias = "sample_interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? SampleIntervalSecs { get; set; }
        [YamlMember(Alias = "training_window_mins", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? TrainingWindowMins { get; set; }
        [YamlMember(Alias = "optimization_interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? OptimizationIntervalSecs { get; set; }
        [YamlMember(Alias = "validation_fraction")] public double ValidationFraction { get; set; } = 0.2;
        [YamlMember(Alias = "minimum_improvement")] public double MinimumImprovement { get; set; } = 0; // How far a proposal's overall score must beat the current calibration's

        // Legacy timing keys. Keep these as deserialization fallbacks for existing installations.
        [YamlMember(Alias = "interval_secs", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? IntervalSecs { get; set; }
        [YamlMember(Alias = "keep_snapshot_mins", DefaultValuesHandling = DefaultValuesHandling.OmitNull)] public int? KeepSnapshotMins { get; set; }
        [YamlMember(Alias = "min_samples")] public int MinSamples { get; set; } = 5; // Minimum holdout measurements a candidate must be evaluated on before it can be applied
        // What decides whether a proposal is applied: "links" (held-out node-to-node links; the default) or "spots"
        // (devices at known positions, each spot's level fitted; see OptimizationCycle and #1868)
        [YamlMember(Alias = "judge")] public string Judge { get; set; } = "links";
        [YamlIgnore] public bool JudgeOnSpots => string.Equals(Judge?.Trim(), "spots", StringComparison.OrdinalIgnoreCase);
        [YamlMember(Alias = "limits")] public Dictionary<string, double> Limits { get; set; } = new();
        [YamlMember(Alias = "weights")] public Dictionary<string, double> Weights { get; set; } = new();

        [YamlIgnore] public int EffectiveSampleIntervalSecs => Math.Max(1, SampleIntervalSecs ?? 30);
        [YamlIgnore] public int EffectiveTrainingWindowMins => Math.Max(1, TrainingWindowMins ?? KeepSnapshotMins ?? 30);
        [YamlIgnore] public int EffectiveOptimizationIntervalSecs => Math.Max(1, OptimizationIntervalSecs ?? IntervalSecs ?? 900);
        [YamlIgnore] public double EffectiveValidationFraction => Math.Clamp(ValidationFraction, 0.1, 0.5);

        [YamlIgnore] public double AbsorptionMin => Limits.TryGetValue("absorption_min", out var val) ? val : 2;
        [YamlIgnore] public double AbsorptionMax => Limits.TryGetValue("absorption_max", out var val) ? val : 4;
        [YamlIgnore] public double TxRefRssiMin => Limits.TryGetValue("tx_ref_rssi_min", out var val) ? val : -70;
        [YamlIgnore] public double TxRefRssiMax => Limits.TryGetValue("tx_ref_rssi_max", out var val) ? val : -50;
        [YamlIgnore] public double RxAdjRssiMin => Limits.TryGetValue("rx_adj_rssi_min", out var val) ? val : -5;
        [YamlIgnore] public double RxAdjRssiMax => Limits.TryGetValue("rx_adj_rssi_max", out var val) ? val : 30;

        [YamlIgnore] public double CorrelationWeight => Weights.TryGetValue("correlation", out var val) ? val : 0.5;
        [YamlIgnore] public double RmseWeight => Weights.TryGetValue("rmse", out var val) ? val : 0.5;

        /// <summary>
        /// Weight of the device-residual term in a proposal's composite score: device_weight x (1 - e / (1 + e)),
        /// e the mean |leave-one-out residual| in dB over held-out device windows. 0 scores node-to-node only.
        /// </summary>
        [YamlMember(Alias = "device_weight")] public double DeviceWeight { get; set; } = 1.0;

        /// <summary>Settings for the device_residual optimizer and the device windows it trains on.</summary>
        [YamlMember(Alias = "device_residual")] public ConfigDeviceResidual DeviceResidual { get; set; } = new();

        /// <summary>
        /// Whether located devices' readings are being windowed for the device_residual optimizer. Only that
        /// optimizer uses them, so other modes keep no device windows and score exactly as before.
        /// </summary>
        [YamlIgnore] public bool CollectsDeviceWindows => Enabled && DeviceResidualMode;

        /// <summary>Whether the configured optimizer mode is device_residual.</summary>
        [YamlIgnore] public bool DeviceResidualMode => string.Equals(Optimizer?.Trim(), "device_residual", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How located devices' readings are turned into training windows for the device_residual optimizer
    /// (see <c>DeviceResidualStore</c>), and when a learned device rssi@1m is written back.
    /// </summary>
    public partial class ConfigDeviceResidual
    {
        /// <summary>Readings from one device are pooled per node over this many seconds; each node's median is one sample.</summary>
        [YamlMember(Alias = "window_secs")] public int WindowSecs { get; set; } = 10;

        /// <summary>A window is kept only if at least this many stationary, positioned nodes heard the device in it.</summary>
        [YamlMember(Alias = "min_nodes")] public int MinNodes { get; set; } = 8;

        /// <summary>How long windows are kept for training.</summary>
        [YamlMember(Alias = "history_hours")] public double HistoryHours { get; set; } = 24;

        /// <summary>A device's learned rssi@1m is only applied once it has this many windows and moves around.</summary>
        [YamlMember(Alias = "min_windows")] public int MinWindows { get; set; } = 50;

        /// <summary>Write learned per-device rssi@1m values to device settings; false only reports them.</summary>
        [YamlMember(Alias = "apply_device_ref")] public bool ApplyDeviceRef { get; set; } = true;

        [YamlIgnore] public TimeSpan Window => TimeSpan.FromSeconds(Math.Clamp(WindowSecs, 1, 3600));
        [YamlIgnore] public int EffectiveMinNodes => Math.Max(4, MinNodes);
        [YamlIgnore] public TimeSpan History => TimeSpan.FromHours(Math.Clamp(HistoryHours, 0.1, 24 * 7));
    }

    public partial class ConfigHistory
    {
        [YamlMember(Alias = "enabled")] public bool Enabled { get; set; } = false;

        // SQLite file; relative paths are under <config dir>/.storage. Read once at startup.
        [YamlMember(Alias = "path")]
        public string Path { get; set; } = "history.db";

        [YamlMember(Alias = "expire_after")] public string ExpireAfter { get; set; } = "24h";

        [YamlIgnore]
        public TimeSpan ExpireAfterTimeSpan => ExpireAfter.TryParseDurationString(out var ts) ? ts : TimeSpan.FromHours(24);
    }

    public partial class ConfigWeighting
    {
        [YamlMember(Alias = "algorithm")]
        public string Algorithm { get; set; } = "gaussian";

        [YamlMember(Alias = "props")] public Dictionary<string, double> Props { get; set; } = new();
    }

    public partial class ConfigGps
    {
        [YamlMember(Alias = "latitude")]
        public double? Latitude { get; set; }
        [YamlMember(Alias = "longitude")]
        public double? Longitude { get; set; }
        [YamlMember(Alias = "elevation")]
        public double? Elevation { get; set; }
        [YamlMember(Alias = "rotation")]
        public double? Rotation { get; set; } // Degrees clockwise from North
        [YamlMember(Alias = "report")]
        public bool Report { get; set; } = false;
    }

    public partial class ConfigMqtt
    {
        public string? Host { get; set; }

        public int? Port { get; set; }

        public bool? Ssl { get; set; }

        [YamlMember(Alias = "insecure")]
        public bool Insecure { get; set; }

        [YamlMember(Alias = "ca_cert_path")]
        public string? CaCertPath { get; set; }

        public string? Username { get; set; }

        // Never serialize the broker password to API responses or MCP clients. Config only leaves through
        // System.Text.Json (Newtonsoft is confined to the files NewtonsoftConventionTests allows, none of
        // which serialize Config). YamlDotNet has its own attributes, so YAML round-tripping is unaffected.
        // Logging is not covered: Serilog's {@} destructuring ignores JSON attributes, so never {@}-log a Config.
        [System.Text.Json.Serialization.JsonIgnore]
        public string? Password { get; set; }

        [YamlMember(Alias = "client_id")]
        public string ClientId { get; set; } = "espresense-companion";

        [YamlMember(Alias = "discovery_topic")]
        public string DiscoveryTopic { get; set; } = "homeassistant";

        /// <summary>
        /// When true the companion subscribes and processes messages but never publishes; each publish
        /// is logged at Information level instead. Defaults to false. Replaces the deprecated heuristic
        /// of a <see cref="ClientId"/> containing "read", which still enables read-only mode but logs a warning.
        /// </summary>
        [YamlMember(Alias = "read_only")]
        public bool ReadOnly { get; set; }
    }

    public partial class ConfigDevice
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigFloor
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

    public partial class ConfigRoom
    {
        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "points")]
        public double[][]? Points { get; set; }

        [YamlMember(Alias = "color")]
        public string? Color { get; set; }

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigNode
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlMember(Alias = "point")]
        public double[]? Point { get; set; }

        [YamlMember(Alias = "floors")]
        public string[]? Floors { get; set; }

        [YamlMember(Alias = "stationary")]
        public bool Stationary { get; set; } = true;

        public string GetId() => Id ?? Name?.ToSnakeCase()?.ToLower() ?? "none";
    }

    public partial class ConfigFiltering
    {
        [YamlMember(Alias = "process_noise")]
        public double ProcessNoise { get; set; } = 0.01;

        [YamlMember(Alias = "measurement_noise")]
        public double MeasurementNoise { get; set; } = 0.1;

        [YamlMember(Alias = "max_velocity")]
        public double MaxVelocity { get; set; } = 0.5;

        [YamlMember(Alias = "smoothing_weight")]
        public double SmoothingWeight { get; set; } = 0.7;

        [YamlMember(Alias = "motion_sigma")]
        public double MotionSigma { get; set; } = 2.0;
    }
}
