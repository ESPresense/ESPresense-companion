using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using Serilog;

namespace ESPresense.Services;

public class LeaseInfo
{
    public string InstanceId { get; set; } = string.Empty;
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
}

public interface ILeaseService
{
    Task<LeaseHandle?> AcquireAsync(string leaseName, int leaseDurationSecs = 120, int renewalIntervalSecs = 30, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string leaseName);
    bool HasLease(string leaseName);
    Task ReleaseAllAsync();
}

public class LeaseService : ILeaseService, IDisposable
{
    private const string LeaseTopicPrefix = "espresense/companion/lease/";
    private readonly IMqttCoordinator _mqttCoordinator;
    private readonly ILogger<LeaseService> _logger;
    private readonly string _instanceId;
    private readonly ConcurrentDictionary<string, LeaseState> _leases = new();
    private readonly ConcurrentDictionary<string, LeaseInfo> _observedLeases = new();

    private class LeaseState
    {
        public LeaseInfo LeaseInfo { get; set; } = new();
        public Timer? RenewalTimer { get; set; }
        public int LeaseDurationSecs { get; set; }
        public int RenewalIntervalSecs { get; set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    public LeaseService(
        IMqttCoordinator mqttCoordinator,
        ILogger<LeaseService> logger)
    {
        _mqttCoordinator = mqttCoordinator;
        _logger = logger;
        var shortId = Guid.NewGuid().ToString("N")[..8]; // Short unique ID
        _instanceId = $"{Environment.MachineName}-{shortId}";

        _logger.LogInformation("LeaseService initialized with instance ID: {InstanceId}", _instanceId);

        // Subscribe to all lease topics to monitor lease holders
        _mqttCoordinator.MqttMessageReceivedAsync += OnMqttMessageReceived;
    }

    private Task OnMqttMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        if (!topic.StartsWith(LeaseTopicPrefix))
            return Task.CompletedTask;

        var leaseName = topic[LeaseTopicPrefix.Length..];

        try
        {
            var payload = args.ApplicationMessage.ConvertPayloadToString();

            if (string.IsNullOrEmpty(payload))
            {
                // Lease was released
                _observedLeases.TryRemove(leaseName, out _);
                _logger.LogDebug("Lease '{LeaseName}' was released", leaseName);
                return Task.CompletedTask;
            }

            var leaseInfo = JsonSerializer.Deserialize<LeaseInfo>(payload);
            if (leaseInfo != null)
            {
                _observedLeases[leaseName] = leaseInfo;

                // Check if our lease expired while we held it
                if (_leases.TryGetValue(leaseName, out var ourLease) &&
                    leaseInfo.InstanceId == _instanceId &&
                    leaseInfo.ExpiresAt < DateTime.UtcNow)
                {
                    _logger.LogWarning(
                        "Our lease '{LeaseName}' expired (instance: {InstanceId})",
                        leaseName,
                        _instanceId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing lease for '{LeaseName}'", leaseName);
        }

        return Task.CompletedTask;
    }

    public async Task<LeaseHandle?> AcquireAsync(
        string leaseName,
        int leaseDurationSecs = 120,
        int renewalIntervalSecs = 30,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseName))
            throw new ArgumentException("Lease name cannot be empty", nameof(leaseName));

        var deadline = timeout.HasValue ? DateTime.UtcNow + timeout.Value : DateTime.MaxValue;
        var retryDelay = TimeSpan.FromSeconds(5);

        while (true)
        {
            var leaseState = _leases.GetOrAdd(leaseName, _ => new LeaseState
            {
                LeaseDurationSecs = leaseDurationSecs,
                RenewalIntervalSecs = renewalIntervalSecs
            });

            await leaseState.Lock.WaitAsync(cancellationToken);
            try
            {
                // Check if we already hold this lease
                if (leaseState.LeaseInfo.InstanceId == _instanceId &&
                    leaseState.LeaseInfo.ExpiresAt > DateTime.UtcNow)
                {
                    await RenewInternalAsync(leaseName, leaseState, cancellationToken);
                    return new LeaseHandle(this, leaseName);
                }

                // Check if another instance holds a valid lease
                if (_observedLeases.TryGetValue(leaseName, out var observedLease))
                {
                    if (observedLease.ExpiresAt > DateTime.UtcNow)
                    {
                        // Lease is held by another instance
                        if (DateTime.UtcNow >= deadline)
                        {
                            _logger.LogInformation(
                                "Timeout waiting for lease '{LeaseName}' (held by instance {HolderId})",
                                leaseName,
                                observedLease.InstanceId);
                            return null;
                        }

                        // Wait and retry
                        _logger.LogDebug(
                            "Lease '{LeaseName}' held by instance {HolderId}, expires at {ExpiresAt}, retrying...",
                            leaseName,
                            observedLease.InstanceId,
                            observedLease.ExpiresAt);
                    }
                    else
                    {
                        // Lease expired, we can take over
                        _logger.LogInformation(
                            "Taking over expired lease '{LeaseName}' from instance {HolderId}",
                            leaseName,
                            observedLease.InstanceId);
                    }
                }

                // Try to acquire new lease if not held or expired
                if (!_observedLeases.TryGetValue(leaseName, out var currentLease) ||
                    currentLease.ExpiresAt <= DateTime.UtcNow)
                {
                    var lease = new LeaseInfo
                    {
                        InstanceId = _instanceId,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(leaseDurationSecs)
                    };

                    await EnsureMqttReadyAsync(cancellationToken);

                    var topic = LeaseTopicPrefix + leaseName;
                    var payload = JsonSerializer.Serialize(lease);
                    await _mqttCoordinator.EnqueueAsync(topic, payload, retain: true);

                    leaseState.LeaseInfo = lease;
                    leaseState.LeaseDurationSecs = leaseDurationSecs;
                    leaseState.RenewalIntervalSecs = renewalIntervalSecs;

                    _logger.LogInformation(
                        "Acquired lease '{LeaseName}' (instance: {InstanceId}, expires: {ExpiresAt})",
                        leaseName,
                        _instanceId,
                        lease.ExpiresAt);

                    // Start renewal timer
                    StartRenewalTimer(leaseName, leaseState);

                    return new LeaseHandle(this, leaseName);
                }
            }
            finally
            {
                leaseState.Lock.Release();
            }

            // Wait before retrying
            var remainingTime = deadline - DateTime.UtcNow;
            var delayTime = remainingTime < retryDelay ? remainingTime : retryDelay;

            if (delayTime > TimeSpan.Zero)
            {
                await Task.Delay(delayTime, cancellationToken);
            }
            else
            {
                // Timeout reached
                return null;
            }
        }
    }

    private async Task RenewInternalAsync(string leaseName, LeaseState leaseState, CancellationToken cancellationToken = default)
    {
        if (leaseState.LeaseInfo.InstanceId != _instanceId)
        {
            _logger.LogWarning("Attempted to renew lease '{LeaseName}' but we don't own it", leaseName);
            return;
        }

        var lease = new LeaseInfo
        {
            InstanceId = _instanceId,
            ExpiresAt = DateTime.UtcNow.AddSeconds(leaseState.LeaseDurationSecs)
        };

        await EnsureMqttReadyAsync(cancellationToken);

        var topic = LeaseTopicPrefix + leaseName;
        var payload = JsonSerializer.Serialize(lease);
        await _mqttCoordinator.EnqueueAsync(topic, payload, retain: true);

        leaseState.LeaseInfo = lease;

        _logger.LogDebug(
            "Renewed lease '{LeaseName}' (instance: {InstanceId}, expires: {ExpiresAt})",
            leaseName,
            _instanceId,
            lease.ExpiresAt);
    }

    public async Task ReleaseAsync(string leaseName)
    {
        if (!_leases.TryGetValue(leaseName, out var leaseState))
            return;

        await leaseState.Lock.WaitAsync();
        try
        {
            if (leaseState.LeaseInfo.InstanceId != _instanceId)
                return;

            StopRenewalTimer(leaseState);

            // Clear the retained lease message (MQTT should already be ready from acquire)
            var topic = LeaseTopicPrefix + leaseName;
            await _mqttCoordinator.EnqueueAsync(topic, null, retain: true);

            _leases.TryRemove(leaseName, out _);

            _logger.LogInformation("Released lease '{LeaseName}' (instance: {InstanceId})", leaseName, _instanceId);
        }
        finally
        {
            leaseState.Lock.Release();
        }
    }

    public bool HasLease(string leaseName)
    {
        if (!_leases.TryGetValue(leaseName, out var leaseState))
            return false;

        return leaseState.LeaseInfo.InstanceId == _instanceId &&
               leaseState.LeaseInfo.ExpiresAt > DateTime.UtcNow;
    }

    public async Task ReleaseAllAsync()
    {
        var tasks = new List<Task>();
        foreach (var leaseName in _leases.Keys.ToList())
        {
            tasks.Add(ReleaseAsync(leaseName));
        }
        await Task.WhenAll(tasks);
    }

    private async Task EnsureMqttReadyAsync(CancellationToken cancellationToken = default)
    {
        // Always wait for MQTT - if already connected, this completes immediately
        // If disconnected, this will wait for reconnection
        await _mqttCoordinator.WaitForConnectionAsync(cancellationToken);
    }

    private void StartRenewalTimer(string leaseName, LeaseState leaseState)
    {
        StopRenewalTimer(leaseState);

        leaseState.RenewalTimer = new Timer(
            async _ =>
            {
                try
                {
                    await leaseState.Lock.WaitAsync();
                    try
                    {
                        await RenewInternalAsync(leaseName, leaseState);
                    }
                    finally
                    {
                        leaseState.Lock.Release();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error renewing lease '{LeaseName}'", leaseName);
                }
            },
            null,
            TimeSpan.FromSeconds(leaseState.RenewalIntervalSecs),
            TimeSpan.FromSeconds(leaseState.RenewalIntervalSecs));
    }

    private void StopRenewalTimer(LeaseState leaseState)
    {
        leaseState.RenewalTimer?.Dispose();
        leaseState.RenewalTimer = null;
    }

    public void Dispose()
    {
        _mqttCoordinator.MqttMessageReceivedAsync -= OnMqttMessageReceived;

        foreach (var leaseState in _leases.Values)
        {
            StopRenewalTimer(leaseState);
            leaseState.Lock?.Dispose();
        }
        _leases.Clear();
    }
}
