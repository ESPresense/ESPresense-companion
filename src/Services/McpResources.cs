using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using ESPresense.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using AutoMapper;

namespace ESPresense.Services;

[McpServerResourceType]
[McpServerToolType]
public class McpResources
{
    private readonly State _state;
    private readonly ConfigLoader _config;
    private readonly NodeSettingsStore _nsd;
    private readonly DeviceSettingsStore _dss;
    private readonly TelemetryService _telemetryService;
    private readonly IMapper _mapper;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpResources(State state, ConfigLoader config, NodeSettingsStore nsd, DeviceSettingsStore dss, TelemetryService telemetryService, IMapper mapper)
    {
        _state = state;
        _config = config;
        _nsd = nsd;
        _dss = dss;
        _telemetryService = telemetryService;
        _mapper = mapper;
        _jsonOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    [McpServerResource(UriTemplate = "state://config", Name = "Application Configuration", MimeType = "application/json")]
    [Description("Get the current application configuration")]
    public Task<string> GetConfigResource()
    {
        return Task.FromResult(JsonSerializer.Serialize(_config.Config, _jsonOptions));
    }

    [McpServerTool(Name = "get_config")]
    [Description("Get the current configuration")]
    public Task<string> GetConfigTool()
    {
        return Task.FromResult(JsonSerializer.Serialize(_config.Config, _jsonOptions));
    }

    [McpServerResource(UriTemplate = "state://nodes", Name = "Nodes Status", MimeType = "application/json")]
    [Description("Get the list of nodes and their current status")]
    public Task<string> GetNodesResource()
    {
        var nodes = _mapper.Map<IEnumerable<NodeStateTele>>(_state.Nodes.Values);
        return Task.FromResult(JsonSerializer.Serialize(nodes, _jsonOptions));
    }

    [McpServerTool(Name = "get_nodes")]
    [Description("Get the list of nodes and their status")]
    public Task<string> GetNodesTool()
    {
        var nodes = _mapper.Map<IEnumerable<NodeStateTele>>(_state.Nodes.Values);
        return Task.FromResult(JsonSerializer.Serialize(nodes, _jsonOptions));
    }

    [McpServerResource(UriTemplate = "state://devices", Name = "Devices Status", MimeType = "application/json")]
    [Description("Get the list of tracked devices and their locations")]
    public Task<string> GetDevicesResource()
    {
        var devices = _state.Devices.Values.ToList();
        EnrichDevices(devices);
        return Task.FromResult(JsonSerializer.Serialize(devices, _jsonOptions));
    }

    [McpServerTool(Name = "get_devices")]
    [Description("Get the list of devices")]
    public Task<string> GetDevicesTool()
    {
        var devices = _state.Devices.Values.ToList();
        EnrichDevices(devices);
        return Task.FromResult(JsonSerializer.Serialize(devices, _jsonOptions));
    }

    [McpServerResource(UriTemplate = "state://telemetry", Name = "System Telemetry", MimeType = "application/json")]
    [Description("Get the current system telemetry and performance metrics")]
    public Task<string> GetTelemetryResource()
    {
        return Task.FromResult(JsonSerializer.Serialize(_telemetryService.Telemetry, _jsonOptions));
    }

    [McpServerTool(Name = "get_telemetry")]
    [Description("Get the current telemetry data")]
    public Task<string> GetTelemetryTool()
    {
        return Task.FromResult(JsonSerializer.Serialize(_telemetryService.Telemetry, _jsonOptions));
    }

    [McpServerResource(UriTemplate = "state://calibration", Name = "Calibration Data", MimeType = "application/json")]
    [Description("Get the current calibration data and accuracy metrics")]
    public Task<string> GetCalibrationResource()
    {
        var c = CalculateCalibration();
        return Task.FromResult(JsonSerializer.Serialize(c, _jsonOptions));
    }

    [McpServerTool(Name = "get_calibration")]
    [Description("Get the calibration data")]
    public Task<string> GetCalibrationTool()
    {
        var c = CalculateCalibration();
        return Task.FromResult(JsonSerializer.Serialize(c, _jsonOptions));
    }

    private Calibration CalculateCalibration()
    {
        var c = new Calibration();
        c.OptimizerState = _state.OptimizerState;

        var mapDistances = new List<double>();
        var actualDistances = new List<double>();

        foreach (var (txId, tx) in _state.Nodes.Where(kv => kv.Value.RxNodes.Values.Any(n => n.Current)).OrderBy(a => a.Value.Name))
        {
            var txNs = _nsd.Get(txId);
            var txM = c.Matrix.GetOrAdd(tx.Name ?? txId);
            foreach (var (rxId, rx) in tx.RxNodes.Where(a => a.Value.Current).OrderBy(a => a.Value.Rx?.Name))
            {
                var rxNs = _nsd.Get(rxId);
                var rxM = txM.GetOrAdd(rx.Rx?.Name ?? rxId);
                if (txNs.Calibration.TxRefRssi is not null) rxM["tx_ref_rssi"] = txNs.Calibration.TxRefRssi.Value;
                if (rxNs.Calibration.RxAdjRssi is not null) rxM["rx_adj_rssi"] = rxNs.Calibration.RxAdjRssi.Value;
                if (rxNs.Calibration.Absorption is not null) rxM["absorption"] = rxNs.Calibration.Absorption.Value;
                rxM["mapDistance"] = rx.MapDistance;
                rxM["distance"] = rx.Distance;
                rxM["rssi"] = rx.Rssi;
                rxM["diff"] = rx.Distance - rx.MapDistance;
                rxM["percent"] = rx.MapDistance != 0 ? ((rx.Distance - rx.MapDistance) / rx.MapDistance) : 0;
                if (rx.DistVar is not null) rxM["var"] = rx.DistVar.Value;

                if (rx.MapDistance > 0 && rx.Distance > 0)
                {
                    mapDistances.Add(rx.MapDistance);
                    actualDistances.Add(rx.Distance);
                }
            }
        }

        c.R = MathUtils.CalculatePearsonCorrelation(mapDistances, actualDistances);
        c.RMSE = MathUtils.CalculateRMSE(mapDistances, actualDistances);
        return c;
    }

    private void EnrichConfiguredRefRssi(Device device)
    {
        var deviceSettings = _dss.Get(device.Id);
        device.ConfiguredRefRssi = deviceSettings?.RefRssi;
        if (!string.IsNullOrWhiteSpace(deviceSettings?.Name))
        {
            device.Name = deviceSettings.Name;
        }
    }

    private void EnrichDevices(IEnumerable<Device> devices)
    {
        foreach (var device in devices)
        {
            EnrichConfiguredRefRssi(device);
        }
    }
}
