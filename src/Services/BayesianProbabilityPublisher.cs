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
    private const int FallbackConfidenceThreshold = 5; // Scenarios with confidence <= this are considered fallbacks (e.g., NearestNode)

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

        // Filter out fallback scenarios (very low confidence like NearestNode) when better locators are working
        // This prevents fallback locators from polluting the probability mix
        var maxConfidence = activeScenarios.Max(s => s.Confidence ?? 0);
        if (maxConfidence > FallbackConfidenceThreshold)
        {
            activeScenarios = activeScenarios.Where(s => (s.Confidence ?? 0) > FallbackConfidenceThreshold).ToList();
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
                device.BayesianProbabilities[roomName] = roundedProbability;
                changed = true;
            }

            var hasDiscovery = device.BayesianDiscoveries.ContainsKey(roomName);

            if (!IsSyntheticRoom(roomName) && probability >= config.DiscoveryThreshold)
            {
                var discovery = device.BayesianDiscoveries.GetOrAdd(roomName, key => CreateProbabilityDiscovery(device, key));
                if (!device.HassAutoDiscovery.Contains(discovery))
                    device.HassAutoDiscovery.Add(discovery);
                await discovery.Send(_mqtt);
            }
            else if (hasDiscovery && IsSyntheticRoom(roomName))
            {
                if (device.BayesianDiscoveries.TryRemove(roomName, out var staleDiscovery))
                {
                    device.HassAutoDiscovery.Remove(staleDiscovery);
                    await staleDiscovery.Delete(_mqtt);
                    changed = true;
                }
            }
        }

        // For rooms no longer in the probability vector:
        // - If they have a discovery (sticky sensor), keep discovery but set probability to 0
        // - If they're synthetic rooms without discovery, just remove them
        foreach (var key in device.BayesianProbabilities.Keys.ToArray())
        {
            if (activeKeys.Contains(key)) continue;

            var hasDiscovery = device.BayesianDiscoveries.ContainsKey(key);

            if (hasDiscovery)
            {
                // Sticky behavior: keep the discovery, just set probability to 0
                if (device.BayesianProbabilities.TryGetValue(key, out var existing) && existing != 0)
                {
                    device.BayesianProbabilities[key] = 0;
                    changed = true;
                }
            }
            else
            {
                // No discovery (synthetic room or never crossed threshold) - safe to remove
                device.BayesianProbabilities.TryRemove(key, out _);
                changed = true;
            }
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
            Name = $"{roomName} Probability",
            UniqueId = $"espresense-companion-{device.Id}-{sanitizedRoom}",
            StateTopic = $"espresense/companion/{device.Id}/attributes",
            ValueTemplate = $"{{{{ value_json.probabilities['{sanitizedRoom}'] | default(0) }}}}",
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


    public static string SanitizeSegment(string value)
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
