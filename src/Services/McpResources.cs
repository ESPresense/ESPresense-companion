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

namespace ESPresense.Services;

[McpServerResourceType]
[McpServerToolType]
public class McpResources
{
    private readonly State _state;
    private readonly ConfigLoader _config;
    private readonly NodeSettingsStore _nsd;
    private readonly NodeTelemetryStore _nts;
    private readonly DeviceSettingsStore _dss;
    private readonly TelemetryService _telemetryService;
    private readonly FirmwareUpdateJobService _firmwareUpdateJobs;
    private readonly FirmwareCatalogService _firmwareCatalog;
    private readonly NodeStateMapper _mapper;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpResources(
        State state,
        ConfigLoader config,
        NodeSettingsStore nsd,
        NodeTelemetryStore nts,
        DeviceSettingsStore dss,
        TelemetryService telemetryService,
        FirmwareUpdateJobService firmwareUpdateJobs,
        FirmwareCatalogService firmwareCatalog,
        NodeStateMapper mapper)
    {
        _state = state;
        _config = config;
        _nsd = nsd;
        _nts = nts;
        _dss = dss;
        _telemetryService = telemetryService;
        _firmwareUpdateJobs = firmwareUpdateJobs;
        _firmwareCatalog = firmwareCatalog;
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
        var nodes = _mapper.ToNodeStateTeles(_state.Nodes.Values);
        return Task.FromResult(JsonSerializer.Serialize(nodes, _jsonOptions));
    }

    [McpServerTool(Name = "get_nodes")]
    [Description("Get the list of nodes and their status")]
    public Task<string> GetNodesTool()
    {
        var nodes = _mapper.ToNodeStateTeles(_state.Nodes.Values);
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

    [McpServerTool(Name = "get_node_settings")]
    [Description("Get the settings for a specific node")]
    public Task<string> GetNodeSettingsTool(
        [Description("Node identifier")] string nodeId)
    {
        var settings = _nsd.Get(nodeId);
        return Task.FromResult(JsonSerializer.Serialize(settings, _jsonOptions));
    }

    [McpServerTool(Name = "set_node_settings")]
    [Description("Set node settings. Only changed values are written via MQTT.")]
    public async Task<string> SetNodeSettingsTool(
        [Description("Node identifier")] string nodeId,
        [Description("Node settings object")] NodeSettings settings)
    {
        settings.Id = nodeId;
        await _nsd.Set(nodeId, settings);
        return JsonSerializer.Serialize(new { ok = true, nodeId }, _jsonOptions);
    }

    [McpServerTool(Name = "restart_node")]
    [Description("Request node restart")]
    public async Task<string> RestartNodeTool(
        [Description("Node identifier")] string nodeId)
    {
        await _nsd.Restart(nodeId);
        return JsonSerializer.Serialize(new { ok = true, nodeId }, _jsonOptions);
    }

    [McpServerTool(Name = "request_node_update")]
    [Description("Request node self-update with optional firmware URL")]
    public async Task<string> RequestNodeUpdateTool(
        [Description("Node identifier")] string nodeId,
        [Description("Optional firmware URL")] string? url = null)
    {
        if (!_firmwareUpdateJobs.IsValidNodeUpdateUrl(url))
        {
            return JsonSerializer.Serialize(
                new { ok = false, error = "Only ESPresense GitHub URLs are allowed" },
                _jsonOptions);
        }

        await _nsd.Update(nodeId, url);
        return JsonSerializer.Serialize(new { ok = true, nodeId, url }, _jsonOptions);
    }

    [McpServerTool(Name = "delete_node")]
    [Description("Delete node settings/telemetry and remove from in-memory state")]
    public async Task<string> DeleteNodeTool(
        [Description("Node identifier")] string nodeId,
        [Description("Allow deleting config-defined nodes")] bool allowConfigNode = false)
    {
        if (_state.Nodes.TryGetValue(nodeId, out var node) && node.SourceType == NodeSourceType.Config && !allowConfigNode)
        {
            return JsonSerializer.Serialize(
                new { ok = false, error = "Node is config-defined. Set allowConfigNode=true to delete." },
                _jsonOptions);
        }

        await _nsd.Delete(nodeId);
        await _nts.Delete(nodeId);
        _state.Nodes.TryRemove(nodeId, out _);
        return JsonSerializer.Serialize(new { ok = true, nodeId }, _jsonOptions);
    }

    [McpServerTool(Name = "get_firmware_types")]
    [Description("List firmware binaries (CPU and flavor) available in each release/artifact")]
    public Task<string> GetFirmwareTypesTool()
    {
        return Task.FromResult(JsonSerializer.Serialize(_firmwareCatalog.GetTypes(), _jsonOptions));
    }

    [McpServerTool(Name = "list_firmware_releases")]
    [Description("List ESPresense firmware releases (newest first). Use the version with start_firmware_update.")]
    public async Task<string> ListFirmwareReleasesTool(
        [Description("Include beta (prerelease) versions")] bool includePrerelease = true,
        [Description("Maximum number of releases to return")] int limit = 10)
    {
        try
        {
            var releases = await _firmwareCatalog.GetReleasesAsync(includePrerelease, limit);
            return JsonSerializer.Serialize(new { ok = true, releases }, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, _jsonOptions);
        }
    }

    [McpServerTool(Name = "list_firmware_artifacts")]
    [Description("List successful ESPresense CI firmware builds (newest first), e.g. to test a pull request. Use the artifactId with start_firmware_update.")]
    public async Task<string> ListFirmwareArtifactsTool(
        [Description("Only builds for this ESPresense pull request number")] int? pullRequest = null,
        [Description("Only builds for this branch, e.g. main")] string? branch = null,
        [Description("Maximum number of builds to return")] int limit = 10)
    {
        try
        {
            var artifacts = await _firmwareCatalog.GetArtifactsAsync(pullRequest, branch, limit);
            return JsonSerializer.Serialize(new { ok = true, artifacts }, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, _jsonOptions);
        }
    }

    [McpServerTool(Name = "start_firmware_update")]
    [Description("Start an OTA firmware update job for a node. Pass a release version or a CI artifactId (or an explicit url). The firmware binary defaults to the node's current CPU/flavor. Returns a job id for polling.")]
    public Task<string> StartFirmwareUpdateTool(
        [Description("Node identifier")] string nodeId,
        [Description("Release version tag, from list_firmware_releases")] string? version = null,
        [Description("CI build id, from list_firmware_artifacts")] long? artifactId = null,
        [Description("Firmware binary name, e.g. esp32-verbose.bin (default: node's current firmware)")] string? firmware = null,
        [Description("Explicit firmware binary URL (ESPresense GitHub URLs only); overrides version/artifactId")] string? url = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            var (resolved, _, resolveError) = _firmwareCatalog.ResolveFirmwareUrl(nodeId, version, artifactId, firmware);
            if (resolveError != null)
                return Task.FromResult(JsonSerializer.Serialize(new { ok = false, error = resolveError }, _jsonOptions));
            url = resolved!;
        }

        var (job, error) = _firmwareUpdateJobs.Start(nodeId, url);
        if (error != null)
        {
            return Task.FromResult(JsonSerializer.Serialize(new { ok = false, error }, _jsonOptions));
        }

        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, job }, _jsonOptions));
    }

    [McpServerTool(Name = "get_firmware_update_job")]
    [Description("Get firmware update job status and logs by job id")]
    public Task<string> GetFirmwareUpdateJobTool(
        [Description("Firmware update job identifier")] string jobId)
    {
        var job = _firmwareUpdateJobs.Get(jobId);
        return Task.FromResult(JsonSerializer.Serialize(new { ok = job != null, job }, _jsonOptions));
    }

    [McpServerTool(Name = "list_firmware_update_jobs")]
    [Description("List firmware update jobs (newest first)")]
    public Task<string> ListFirmwareUpdateJobsTool()
    {
        var jobs = _firmwareUpdateJobs.GetAll();
        return Task.FromResult(JsonSerializer.Serialize(jobs, _jsonOptions));
    }

    [McpServerTool(Name = "cancel_firmware_update_job")]
    [Description("Cancel a running firmware update job")]
    public Task<string> CancelFirmwareUpdateJobTool(
        [Description("Firmware update job identifier")] string jobId)
    {
        var (cancelled, error) = _firmwareUpdateJobs.Cancel(jobId);
        return Task.FromResult(JsonSerializer.Serialize(new { ok = cancelled, error }, _jsonOptions));
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
