using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using Serilog;

namespace ESPresense.Services;

public class LeaseInfo
{
    [JsonPropertyName("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }
}

public class LeaseHandle : IAsyncDisposable
{
    private readonly ILeaseService _leaseService;
    private readonly string _leaseName;
    private bool _disposed;

    internal LeaseHandle(ILeaseService leaseService, string leaseName)
    {
        _leaseService = leaseService;
        _leaseName = leaseName;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _leaseService.ReleaseAsync(_leaseName);
    }

    public bool HasLease()
        => _leaseService.HasLease(_leaseName);
}

public interface ILeaseService
{

    Task<LeaseHandle?> AcquireAsync(string leaseName, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string leaseName);
    bool HasLease(string leaseName);
    Task ReleaseAllAsync();
}

public class LeaseService : ILeaseService, IDisposable
{
    const int leaseDurationSecs = 120;
    const int renewalIntervalSecs = 30;

    private const string TopicPrefix = "espresense/companion/lease/";
    private readonly IMqttCoordinator _mqtt;
    private readonly ILogger<LeaseService> _log;
    private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
    private class LeaseState
    {
        public LeaseInfo Observed { get; set; } = new();
        public LeaseInfo Owned { get; set; } = new();
        public Timer? RenewalTimer { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public bool CompetingLeaseObserved { get; set; }
    }

    private readonly ConcurrentDictionary<string, LeaseState> _leases = new();

    public LeaseService(IMqttCoordinator mqtt, ILogger<LeaseService> log)
    {
        _mqtt = mqtt;
        _log = log;
        _log.LogInformation("LeaseService started – instance: {InstanceId}", _instanceId);

        _mqtt.MqttMessageReceivedAsync += OnMqttMessage;
    }

    private Task OnMqttMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        if (!e.ApplicationMessage.Topic.StartsWith(TopicPrefix)) return Task.CompletedTask;

        var leaseName = e.ApplicationMessage.Topic[TopicPrefix.Length..];
        var payload = e.ApplicationMessage.ConvertPayloadToString();

        var info = string.IsNullOrEmpty(payload)
            ? new LeaseInfo { InstanceId = "nobody", ExpiresAt = DateTime.MinValue }
            : JsonSerializer.Deserialize<LeaseInfo>(payload) ?? new LeaseInfo { InstanceId = "nobody", ExpiresAt = DateTime.MinValue };

        // Get or create lease state to track observed leases from other instances
        var state = _leases.GetOrAdd(leaseName, _ => new LeaseState());

        state.Observed = info;

        // Track if we observed a competing instance's valid lease (for race detection)
        if (info.InstanceId != _instanceId && info.ExpiresAt > DateTime.UtcNow)
        {
            state.CompetingLeaseObserved = true;
        }

        // Drop our lease if: someone else has a valid lease, OR we observed our own expired lease
        var shouldDrop = (info.InstanceId != _instanceId && info.ExpiresAt > DateTime.UtcNow) ||  // Someone else has it
                         (info.InstanceId == _instanceId && info.ExpiresAt <= DateTime.UtcNow);   // Our own expired

        if (shouldDrop)
        {
            _ = Task.Run(async () =>
            {
                await state.Lock.WaitAsync();
                try
                {
                    if (state.Owned.InstanceId == _instanceId)
                    {
                        state.RenewalTimer?.Dispose();
                        state.RenewalTimer = null;
                        state.Owned = new();
                        if (info.InstanceId != _instanceId)
                            _log.LogWarning("Lost lease '{LeaseName}' to {Holder}", leaseName, info.InstanceId);
                        else
                            _log.LogWarning("Lease '{LeaseName}' expired", leaseName);
                    }
                }
                finally { state.Lock.Release(); }
            });
        }

        return Task.CompletedTask;
    }

    public async Task<LeaseHandle?> AcquireAsync(
        string leaseName,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseName)) throw new ArgumentException();

        await _mqtt.WaitForConnectionAsync(ct);

        // Initialize with a "nobody" lease that expires in one renewal interval
        // If a real holder exists, they'll renew within this time
        var state = _leases.GetOrAdd(leaseName, _ => new LeaseState
        {
            Observed = new LeaseInfo
            {
                InstanceId = "nobody",
                ExpiresAt = DateTime.UtcNow.AddSeconds(renewalIntervalSecs)
            }
        });

        var deadline = timeout.HasValue ? DateTime.UtcNow + timeout.Value : DateTime.MaxValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            await state.Lock.WaitAsync(ct);
            try
            {
                // Clear the competing lease flag before we attempt to acquire
                state.CompetingLeaseObserved = false;

                var observed = state.Observed;

                // Lease is still held by someone else - wait for expiry
                if (observed.InstanceId != _instanceId && observed.ExpiresAt > DateTime.UtcNow)
                {
                    var waitTime = observed.ExpiresAt - DateTime.UtcNow;
                    if (waitTime > TimeSpan.FromSeconds(5))
                        waitTime = TimeSpan.FromSeconds(5);

                    _log.LogDebug("Lease '{LeaseName}' held by {Holder} until {Expiry}, waiting {Wait}s",
                        leaseName, observed.InstanceId, observed.ExpiresAt, waitTime.TotalSeconds);
                }
                else
                {
                    // Lease is available (expired or we already own it)
                    var proposed = new LeaseInfo
                    {
                        InstanceId = _instanceId,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSecs)
                    };

                    await _mqtt.EnqueueAsync(
                        TopicPrefix + leaseName,
                        JsonSerializer.Serialize(proposed),
                        retain: true);

                    // Check if we observed a competing lease during the publish (race condition)
                    if (state.CompetingLeaseObserved)
                    {
                        _log.LogWarning("Lost race for lease '{LeaseName}' - competing lease observed during publish", leaseName);
                        // Don't set Owned or start renewal - we lost the race
                        state.CompetingLeaseObserved = false;
                    }
                    else
                    {
                        state.Owned = proposed;
                        StartRenewal(state, leaseName, leaseDurationSecs, renewalIntervalSecs);

                        _log.LogInformation("Acquired lease '{LeaseName}' (expires: {Expiry})",
                            leaseName, proposed.ExpiresAt);

                        return new LeaseHandle(this, leaseName);
                    }
                }
            }
            finally
            {
                state.Lock.Release();
            }

            // If we didn't acquire, wait and try again
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return null;
    }

    private void StartRenewal(LeaseState state, string leaseName, int duration, int interval)
    {
        state.RenewalTimer?.Dispose();
        state.RenewalTimer = new Timer(async _ =>
        {
            var renewal = new LeaseInfo
            {
                InstanceId = _instanceId,
                ExpiresAt = DateTime.UtcNow.AddSeconds(duration)
            };

            try
            {
                await _mqtt.EnqueueAsync(TopicPrefix + leaseName, JsonSerializer.Serialize(renewal), retain: true);
                await state.Lock.WaitAsync();
                state.Owned = renewal;
                state.Lock.Release();
            }
            catch { /* will be detected on next acquire */ }
        }, null, TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
    }

    public async Task ReleaseAsync(string leaseName)
    {
        if (!_leases.TryRemove(leaseName, out var state)) return;

        await state.Lock.WaitAsync();
        try
        {
            state.RenewalTimer?.Dispose();
            state.RenewalTimer = null;
            await _mqtt.EnqueueAsync(TopicPrefix + leaseName, null, retain: true);
            _log.LogInformation("Released lease '{LeaseName}'", leaseName);
        }
        finally
        {
            state.Lock.Release();
        }
    }

    public bool HasLease(string leaseName)
        => _leases.TryGetValue(leaseName, out var s) &&
           s.Owned.InstanceId == _instanceId &&
           s.Owned.ExpiresAt > DateTime.UtcNow;

    public Task ReleaseAllAsync() => Task.WhenAll(_leases.Keys.Select(ReleaseAsync));

    public void Dispose()
    {
        _mqtt.MqttMessageReceivedAsync -= OnMqttMessage;
        foreach (var s in _leases.Values)
            s.RenewalTimer?.Dispose();
        _leases.Clear();
    }
}
