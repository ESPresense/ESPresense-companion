using ESPresense.Events;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>Collects only fresh reports during an explicitly labelled, stationary sample.</summary>
public class FingerprintCollectionService(IMqttCoordinator mqtt, ConfigLoader configLoader)
{
    private readonly object _writeLock = new();

    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(15);

    // Default firmware yields roughly 2.3–2.7 reports/node per 10 s in the robot survey.
    // A 15 s window reaches three reports for 86% of known-dwell links; five is too strict.
    public const int MinimumCalibrationReports = 3;

    public async Task<Dictionary<string, FingerprintReading>> CollectAsync(IEnumerable<string> deviceIds, CancellationToken cancellationToken)
    {
        var ids = deviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readings = new Dictionary<string, (double RawSum, double RssiSum, int Count)>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        Task OnMessage(DeviceMessageEventArgs e)
        {
            if (!ids.Contains(e.DeviceId)) return Task.CompletedTask;
            var raw = e.Payload.Rssi + (e.Payload.RssiRxAdj ?? 0);
            if (!double.IsFinite(raw)) return Task.CompletedTask;
            lock (gate)
            {
                var previous = readings.GetValueOrDefault(e.NodeId);
                readings[e.NodeId] = (previous.RawSum + raw, previous.RssiSum + e.Payload.Rssi, previous.Count + 1);
            }
            return Task.CompletedTask;
        }

        mqtt.DeviceMessageReceivedAsync += OnMessage;
        try
        {
            await Task.Delay(Duration, cancellationToken);
            lock (gate)
                return readings.ToDictionary(p => p.Key, p => new FingerprintReading(p.Value.RawSum / p.Value.Count, p.Value.RssiSum / p.Value.Count, p.Value.Count), StringComparer.OrdinalIgnoreCase);
        }
        finally { mqtt.DeviceMessageReceivedAsync -= OnMessage; }
    }

    public static int? CalculateCalibration(IReadOnlyDictionary<string, FingerprintReading> readings,
        IReadOnlyDictionary<string, CalibrationReference> references)
    {
        double sum = 0, weights = 0;
        foreach (var (id, reading) in readings)
        {
            if (reading.Count < MinimumCalibrationReports || !references.TryGetValue(id, out var reference) ||
                !double.IsFinite(reference.Distance) || reference.Distance < 0.1 ||
                !double.IsFinite(reference.Absorption) || reference.Absorption <= 0 || !double.IsFinite(reading.Rssi)) continue;
            var estimate = reading.Rssi + 10 * reference.Absorption * Math.Log10(reference.Distance);
            var weight = 1 / Math.Max(1, reference.Distance);
            sum += estimate * weight;
            weights += weight;
        }
        return weights > 0 ? (int)Math.Floor(sum / weights + 0.5) : null;
    }

    public void Save(Fingerprint fingerprint, FingerprintConfig config)
    {
        lock (_writeLock)
            new FingerprintMap(Path.Combine(configLoader.ConfigDirectory, config.File)).Append(fingerprint);
    }
    public bool Remove(Guid sampleId, string deviceId, FingerprintConfig config)
    {
        lock (_writeLock)
            return new FingerprintMap(Path.Combine(configLoader.ConfigDirectory, config.File)).Remove(sampleId, deviceId);
    }

}

public sealed record FingerprintReading(double RawRssi, double Rssi, int Count);

public sealed record CalibrationReference(double Distance, double Absorption);
