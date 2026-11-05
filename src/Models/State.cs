using System.Collections.Concurrent;
using ESPresense.Extensions;
using ESPresense.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;

namespace ESPresense.Models;

public class State
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _calibrationMetricsLock = new();
    private double? _bestCalibrationRMSE;
    private double? _bestCalibrationR;

    /// <summary>For tests and tools that build a State by hand: its loggers discard everything.</summary>
    public State(ConfigLoader cl, NodeTelemetryStore nts) : this(cl, nts, NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Initializes a new State, wiring telemetry and configuration handling. DI picks this constructor.
    /// </summary>
    /// <remarks>
    /// Subscribes to the provided ConfigLoader's ConfigChanged event and applies the current configuration if present;
    /// see <see cref="Apply"/>.
    /// </remarks>
    public State(ConfigLoader cl, NodeTelemetryStore nts, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        ScenarioFactory = new ScenarioFactory(this, nts, _loggerFactory);
        cl.ConfigChanged += (_, args) => Apply(args);
        if (cl.Config != null) Apply(cl.Config);
    }

    /// <summary>The current config. Replaced only through <see cref="Apply"/>.</summary>
    public Config? Config { get; private set; }

    /// <summary>
    /// Makes <paramref name="c"/> the current config: updates floors and nodes, rebuilds the tracking rules,
    /// republishes Kalman settings to every device, and marks every device to be re-checked.
    /// </summary>
    internal void Apply(Config c)
    {
        // Build anything that can fail before touching state, so a bad config can't half-apply.
        var trackingRules = DeviceTrackingRules.FromConfig(c, _loggerFactory.CreateLogger<DeviceTrackingRules>());

        Config = c;
        TrackingRules = trackingRules;
        foreach (var cf in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(cf.GetId()).Update(c, cf);
        foreach (var cn in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(cn.GetId(), a => new Node(cn.GetId(), NodeSourceType.Config)).Update(c, cn, GetFloorsByIds(cn.Floors));

        // Update Kalman filter settings and propagate to all devices
        var newKalmanSettings = KalmanFilterSettings.FromConfig(c?.Filtering);
        if (!newKalmanSettings.Equals(KalmanSettings))
        {
            KalmanSettings = newKalmanSettings;
            foreach (var device in Devices.Values)
            {
                device.KalmanFilter.Settings = KalmanSettings;
            }
        }

        foreach (var device in Devices.Values) device.Check = true;
    }

    /// <summary>
    /// Shared Kalman filter settings used by all devices
    /// </summary>
    public KalmanFilterSettings KalmanSettings { get; private set; } = KalmanFilterSettings.Default;

    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Device> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Floor> Floors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The devices / exclude_devices rules from the current config, compiled once per load.</summary>
    public DeviceTrackingRules TrackingRules { get; private set; } = DeviceTrackingRules.Empty;

    public OptimizerState OptimizerState { get; set; } = new();
    public LocatorState LocatorState { get; } = new();

    public (double? BestRMSE, double? BestR) RecordCalibrationMetrics(double? rmse, double? r)
    {
        lock (_calibrationMetricsLock)
        {
            if (rmse is { } currentRMSE && double.IsFinite(currentRMSE) &&
                (_bestCalibrationRMSE == null || currentRMSE < _bestCalibrationRMSE))
                _bestCalibrationRMSE = currentRMSE;

            if (r is { } currentR && double.IsFinite(currentR) &&
                (_bestCalibrationR == null || currentR > _bestCalibrationR))
                _bestCalibrationR = currentR;

            return (_bestCalibrationRMSE, _bestCalibrationR);
        }
    }

    public void ResetCalibrationMetrics()
    {
        lock (_calibrationMetricsLock)
        {
            _bestCalibrationRMSE = null;
            _bestCalibrationR = null;
        }
    }

    /// <summary>
    /// The current node-to-node and anchor measurements, without adding them to the optimizer's history
    /// </summary>
    public OptimizationSnapshot ReadMeasures()
    {
        Dictionary<string, OptNode> nodes = new();
        var os = new OptimizationSnapshot
        {
            Timestamp = DateTime.UtcNow
        };
        foreach (var (txId, txNode) in Nodes)
            foreach (var (rxId, meas) in txNode.RxNodes)
            {
                if (meas.Rx == null) continue;
                var tx = nodes.GetOrAdd(txId, a => new OptNode { Id = txId, Name = txNode.Name, Location = txNode.Location });
                var rx = nodes.GetOrAdd(rxId, a => new OptNode { Id = rxId, Name = meas.Rx.Name, Location = meas.Rx.Location });
                tx.Location = txNode.Location;
                rx.Location = meas.Rx.Location;
                if (meas.Current)
                {
                    os.Measures.Add(new Measure()
                    {
                        Distance = meas.Distance,
                        DistVar = meas.DistVar,
                        Rssi = meas.Rssi,
                        RssiRxAdj = meas.RssiRxAdj,
                        RssiVar = meas.RssiVar,
                        RefRssi = meas.RefRssi,
                        Tx = tx,
                        Rx = rx,
                    });
                }
            }

        foreach (var device in Devices.Values.Where(d => d.IsAnchored && d.Anchor != null))
        {
            var anchorLocation = device.Anchor!.Location;
            var anchorNode = nodes.GetOrAdd(device.Id, _ => new OptNode { Id = device.Id, Name = device.Name ?? device.Id, Location = anchorLocation });
            anchorNode.Location = anchorLocation;
            anchorNode.IsAnchor = true;
            anchorNode.Name = device.Name ?? device.Id;

            // The optimizers hold an anchor's txRef at the rssi@1m it was heard with, so a reading
            // without one (0, as Device.MeasuredRefRssi treats it) would pin the fit to nonsense.
            foreach (var deviceNode in device.Nodes.Values.Where(dn => dn.Node?.HasLocation == true && dn.Current
                                                                       && dn.RefRssi != 0 && double.IsFinite(dn.RefRssi)))
            {
                var rxNode = deviceNode.Node!;
                var optRx = nodes.GetOrAdd(rxNode.Id, _ => new OptNode { Id = rxNode.Id, Name = rxNode.Name, Location = rxNode.Location });
                optRx.Location = rxNode.Location;
                optRx.Name = rxNode.Name;

                os.Measures.Add(new Measure
                {
                    Distance = deviceNode.Distance,
                    DistVar = deviceNode.DistVar,
                    Rssi = deviceNode.Rssi,
                    RssiRxAdj = deviceNode.RssiRxAdj,
                    RssiVar = deviceNode.RssiVar,
                    RefRssi = deviceNode.RefRssi,
                    Tx = anchorNode,
                    Rx = optRx,
                });
            }
        }

        return os;
    }

    internal IEnumerable<Floor> GetFloorsByIds(string[]? floorIds)
    {
        if (floorIds == null)
        {
            foreach (var floor in Floors.Values)
                yield return floor;
        }
        else
            foreach (var floorId in floorIds)
                if (Floors.TryGetValue(floorId, out var floor))
                    yield return floor;
    }

    /// <summary>Builds the scenarios a device is located with. Locators, their order and the fallback live in <see cref="ScenarioFactory"/>.</summary>
    public ScenarioFactory ScenarioFactory { get; }

    public IEnumerable<Scenario> GetScenarios(Device device) => ScenarioFactory.Create(device);

    /// <summary>Whether <paramref name="device"/> should be tracked. Pure: the configured name is not applied.</summary>
    public bool ShouldTrack(Device device) => TrackingRules.ShouldTrack(device.Id, device.Name).Track;

    /// <inheritdoc cref="DeviceTrackingRules.IsExcluded"/>
    public bool IsExcluded(string? id, string? name) => TrackingRules.IsExcluded(id, name);
}
