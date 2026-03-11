using System.Collections.Concurrent;
using System.IO.Compression;
using ESPresense.Extensions;
using ESPresense.Network;

namespace ESPresense.Services;

public class FirmwareUpdateJobService
{
    private static readonly string[] TrustedFirmwarePrefixes =
    {
        "https://github.com/ESPresense/",
        "https://nightly.link/ESPresense/"
    };

    private readonly ConcurrentDictionary<string, FirmwareUpdateJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeJobByNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly NodeSettingsStore _nodeSettingsStore;
    private readonly NodeTelemetryStore _nodeTelemetryStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<FirmwareUpdateJobService> _logger;

    public FirmwareUpdateJobService(
        NodeSettingsStore nodeSettingsStore,
        NodeTelemetryStore nodeTelemetryStore,
        HttpClient httpClient,
        ILogger<FirmwareUpdateJobService> logger)
    {
        _nodeSettingsStore = nodeSettingsStore;
        _nodeTelemetryStore = nodeTelemetryStore;
        _httpClient = httpClient;
        _logger = logger;
    }

    public IReadOnlyCollection<FirmwareUpdateJob> GetAll()
    {
        return _jobs.Values.OrderByDescending(a => a.CreatedAt).ToArray();
    }

    public FirmwareUpdateJob? Get(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public bool IsTrustedFirmwareUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return TrustedFirmwarePrefixes.Any(url.StartsWith);
    }

    public (FirmwareUpdateJob? Job, string? Error) Start(string nodeId, string url)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return (null, "nodeId is required");

        if (string.IsNullOrWhiteSpace(url))
            return (null, "url is required");

        if (!IsTrustedFirmwareUrl(url))
            return (null, "Only ESPresense GitHub URLs are allowed");

        if (_activeJobByNode.ContainsKey(nodeId))
            return (null, $"A firmware update is already running for node '{nodeId}'");

        var jobId = Guid.NewGuid().ToString("N");
        var job = new FirmwareUpdateJob
        {
            JobId = jobId,
            NodeId = nodeId,
            Url = url,
            Status = FirmwareUpdateJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        if (!_jobs.TryAdd(jobId, job))
            return (null, "Unable to create update job");

        _activeJobByNode[nodeId] = jobId;
        var cts = new CancellationTokenSource();
        _tokens[jobId] = cts;

        _ = Task.Run(() => ExecuteAsync(job, cts.Token), CancellationToken.None);
        return (job, null);
    }

    public (bool Cancelled, string? Error) Cancel(string jobId)
    {
        if (!_tokens.TryGetValue(jobId, out var cts))
            return (false, "job not found or already finished");

        cts.Cancel();
        return (true, null);
    }

    private async Task ExecuteAsync(FirmwareUpdateJob job, CancellationToken ct)
    {
        UpdateJob(job, FirmwareUpdateJobStatus.Running);
        await AddLog(job, "Starting firmware update", 0);

        try
        {
            ct.ThrowIfCancellationRequested();
            var telemetry = _nodeTelemetryStore.Get(job.NodeId);
            if (telemetry?.Ip == null)
                throw new InvalidOperationException("Telemetry not found for node or node has no IP");

            var ms = await GetFirmware(job.Url, ct);
            await _nodeSettingsStore.Arduino(job.NodeId, true);
            try
            {
                var otaPort = Environment.GetEnvironmentVariable("OTA_UPDATE_PORT").ToInt();
                var espOta = new ESPOta(ms, otaPort, (message, percent) => AddLog(job, message, percent));
                var success = await espOta.Update(telemetry.Ip, 3232, EspOtaCommand.Flash, ct);
                if (!success)
                    throw new InvalidOperationException("Firmware update failed");
            }
            finally
            {
                await _nodeSettingsStore.Arduino(job.NodeId, false);
            }

            await AddLog(job, "Firmware update completed", 100);
            UpdateJob(job, FirmwareUpdateJobStatus.Succeeded);
        }
        catch (OperationCanceledException)
        {
            await AddLog(job, "Firmware update cancelled", -1);
            UpdateJob(job, FirmwareUpdateJobStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating firmware for node {NodeId}", job.NodeId);
            await AddLog(job, ex.Message, -1);
            UpdateJob(job, FirmwareUpdateJobStatus.Failed, ex.Message);
        }
        finally
        {
            job.CompletedAt = DateTime.UtcNow;
            _tokens.TryRemove(job.JobId, out var cts);
            cts?.Dispose();
            _activeJobByNode.TryRemove(job.NodeId, out _);
        }
    }

    private void UpdateJob(FirmwareUpdateJob job, FirmwareUpdateJobStatus status, string? error = null)
    {
        lock (job)
        {
            job.Status = status;
            job.Error = error;
            if (status is FirmwareUpdateJobStatus.Succeeded or FirmwareUpdateJobStatus.Failed or FirmwareUpdateJobStatus.Cancelled)
            {
                job.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    private Task AddLog(FirmwareUpdateJob job, string message, int percentComplete)
    {
        lock (job)
        {
            job.PercentComplete = percentComplete;
            job.Logs.Add(new FirmwareUpdateLogEntry
            {
                At = DateTime.UtcNow,
                Message = message,
                PercentComplete = percentComplete
            });
        }

        return Task.CompletedTask;
    }

    private async Task<MemoryStream> GetFirmware(string url, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Error loading firmware, status={response.StatusCode}");

        var ms1 = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(ms1, ct);
        ms1.Position = 0;

        var buffer = new byte[2];
        _ = await ms1.ReadAsync(buffer, 0, 2, ct);
        ms1.Position = 0;
        var isZip = buffer[0] == 0x50 && buffer[1] == 0x4B;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!isZip && mediaType != "application/zip")
            return ms1;

        using var zipArchive = new ZipArchive(ms1);
        foreach (var entry in zipArchive.Entries)
        {
            var ms2 = new MemoryStream();
            await using var entryStream = entry.Open();
            await entryStream.CopyToAsync(ms2, ct);
            ms2.Position = 0;
            return ms2;
        }

        ms1.Position = 0;
        return ms1;
    }
}

public class FirmwareUpdateJob
{
    public string JobId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Url { get; set; } = "";
    public FirmwareUpdateJobStatus Status { get; set; }
    public int PercentComplete { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<FirmwareUpdateLogEntry> Logs { get; set; } = new();
}

public class FirmwareUpdateLogEntry
{
    public DateTime At { get; set; }
    public string Message { get; set; } = "";
    public int PercentComplete { get; set; }
}

public enum FirmwareUpdateJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
