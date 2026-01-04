using ESPresense.Models;
using System.Globalization;
using System.Text;
using TextExtensions;

namespace ESPresense.Services;

/// <summary>
/// Service responsible for publishing Bayesian room probability data to MQTT
/// and managing Home Assistant auto-discovery for probability sensors.
/// </summary>
public class BayesianProbabilityPublisher
{
    private const double NormalizationEpsilon = 0.0001; // Minimum remainder to add to "other" room (matches 4-decimal precision)
    private const double DiscoveryHysteresisRatio = 0.8; // Remove sensors at 80% of discovery threshold to prevent flapping

    private readonly MqttCoordinator _mqtt;

    public BayesianProbabilityPublisher(MqttCoordinator mqtt)
    {
        _mqtt = mqtt;
    }

    /// <summary>
    /// Builds a normalized probability vector from device scenarios,
    /// grouping probabilities by room name.
    /// </summary>
    public Dictionary<string, double> BuildProbabilityVector(Device device, Scenario? bestScenario)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var activeScenarios = device.Scenarios.Where(s => s.Current && s.Probability > 0).ToList();
        if (activeScenarios.Count == 0 || bestScenario == null)
        {
            result["not_home"] = 1;
            return result;
        }

        foreach (var scenario in activeScenarios)
        {
            var key = scenario.Room?.Name ?? scenario.Name ?? scenario.Floor?.Name ?? "unknown";
            if (string.IsNullOrWhiteSpace(key)) key = "unknown";

            result[key] = result.TryGetValue(key, out var existing)
                ? existing + scenario.Probability
                : scenario.Probability;
        }

        var sum = result.Values.Sum();
        if (sum <= 0)
        {
            result.Clear();
            result["other"] = 1;
            return result;
        }

        foreach (var key in result.Keys.ToList())
        {
            var normalized = result[key] / sum;
            result[key] = Math.Clamp(normalized, 0, 1);
        }

        var normalizedSum = result.Values.Sum();
        if (normalizedSum < 1)
        {
            var remainder = Math.Max(0, 1 - normalizedSum);
            if (remainder > NormalizationEpsilon)
            {
                result["other"] = result.TryGetValue("other", out var other) ? other + remainder : remainder;
            }
        }
        else if (normalizedSum > 1)
        {
            foreach (var key in result.Keys.ToList())
            {
                result[key] /= normalizedSum;
            }
        }

        return result;
    }

    /// <summary>
    /// Publishes probability sensors to MQTT and manages Home Assistant discovery.
    /// Returns true if any probability values changed.
    /// </summary>
    public async Task<bool> PublishProbabilitySensorsAsync(Device device, IReadOnlyDictionary<string, double> probabilities, ConfigBayesianProbabilities config)
    {
        var changed = false;
        var activeKeys = new HashSet<string>(probabilities.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var (roomName, probability) in probabilities)
        {
            var roundedProbability = Math.Round(probability, 4);
            // Direct equality comparison since both values are rounded to 4 decimals
            var payloadChanged = !device.BayesianProbabilities.TryGetValue(roomName, out var existing) || Math.Round(existing, 4) != roundedProbability;
            if (payloadChanged)
            {
                var payload = roundedProbability.ToString("0.####", CultureInfo.InvariantCulture);
                await _mqtt.EnqueueAsync(BuildProbabilityTopic(device.Id, roomName), payload, retain: config.Retain);
                device.BayesianProbabilities[roomName] = roundedProbability;
                changed = true;
            }

            // Use hysteresis to prevent sensor flapping: create at threshold, remove at 80% of threshold
            var removalThreshold = config.DiscoveryThreshold * DiscoveryHysteresisRatio;
            var hasDiscovery = device.BayesianDiscoveries.ContainsKey(roomName);

            if (!IsSyntheticRoom(roomName) && probability >= config.DiscoveryThreshold)
            {
                var discovery = device.BayesianDiscoveries.GetOrAdd(roomName, key => CreateProbabilityDiscovery(device, key));
                if (!device.HassAutoDiscovery.Contains(discovery))
                    device.HassAutoDiscovery.Add(discovery);
                await discovery.Send(_mqtt);
            }
            else if (hasDiscovery && (IsSyntheticRoom(roomName) || probability < removalThreshold))
            {
                if (device.BayesianDiscoveries.TryRemove(roomName, out var staleDiscovery))
                {
                    device.HassAutoDiscovery.Remove(staleDiscovery);
                    await staleDiscovery.Delete(_mqtt);
                    changed = true;
                }
            }
        }

        foreach (var key in device.BayesianProbabilities.Keys.ToArray())
        {
            if (activeKeys.Contains(key)) continue;

            await _mqtt.EnqueueAsync(BuildProbabilityTopic(device.Id, key), null, retain: true);
            device.BayesianProbabilities.TryRemove(key, out _);

            if (device.BayesianDiscoveries.TryRemove(key, out var discovery))
            {
                device.HassAutoDiscovery.Remove(discovery);
                await discovery.Delete(_mqtt);
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Clears all probability outputs and discoveries for a device.
    /// Returns true if any cleanup was performed.
    /// </summary>
    public async Task<bool> ClearProbabilityOutputsAsync(Device device)
    {
        var changed = false;

        foreach (var key in device.BayesianProbabilities.Keys.ToArray())
        {
            await _mqtt.EnqueueAsync(BuildProbabilityTopic(device.Id, key), null, retain: true);
            device.BayesianProbabilities.TryRemove(key, out _);
            changed = true;
        }

        foreach (var entry in device.BayesianDiscoveries.ToArray())
        {
            device.HassAutoDiscovery.Remove(entry.Value);
            await entry.Value.Delete(_mqtt);
            device.BayesianDiscoveries.TryRemove(entry.Key, out _);
            changed = true;
        }

        return changed;
    }

    private AutoDiscovery CreateProbabilityDiscovery(Device device, string roomName)
    {
        var sanitizedRoom = SanitizeSegment(roomName);
        var discoveryId = ($"espresense-{device.Id}-{sanitizedRoom}-probability").ToSnakeCase() ?? $"espresense-{device.Id}-{sanitizedRoom}-probability";

        var record = new AutoDiscovery.DiscoveryRecord
        {
            Name = $"{device.Name ?? device.Id} {roomName} Probability",
            UniqueId = $"espresense-companion-{device.Id}-{sanitizedRoom}",
            StateTopic = BuildProbabilityTopic(device.Id, roomName),
            EntityStatusTopic = "espresense/companion/status",
            Device = new AutoDiscovery.DeviceRecord
            {
                Name = device.Name ?? device.Id,
                Manufacturer = "ESPresense",
                Model = "Companion",
                SwVersion = "1.0.0",
                Identifiers = new[] { $"espresense-{device.Id}" }
            },
            Origin = new AutoDiscovery.OriginRecord { Name = "ESPresense Companion" },
            StateClass = "measurement",
            Icon = "mdi:account-location"
        };

        return new AutoDiscovery("sensor", discoveryId, record);
    }

    private static string BuildProbabilityTopic(string deviceId, string roomName)
    {
        var segment = SanitizeSegment(roomName);
        return $"espresense/companion/{deviceId}/probabilities/{segment}";
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var snake = value.ToSnakeCase();
        var working = !string.IsNullOrWhiteSpace(snake) ? snake! : value;

        var builder = new StringBuilder();
        foreach (var c in working.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
                builder.Append(c);
            else
                builder.Append('_');
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    /// <summary>
    /// Determines if a room name is synthetic (not a real room).
    /// Synthetic rooms like "other" and "not_home" are used for probability normalization
    /// but are excluded from Home Assistant discovery to avoid cluttering the UI.
    /// Note: This means visible HA sensors may not sum to 1.0 when "other" has non-zero probability.
    /// </summary>
    private static bool IsSyntheticRoom(string roomName)
        => roomName.Equals("other", StringComparison.OrdinalIgnoreCase) || roomName.Equals("not_home", StringComparison.OrdinalIgnoreCase);
}
