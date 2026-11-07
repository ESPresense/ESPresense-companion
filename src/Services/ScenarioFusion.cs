using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>What one fusion pass chose for a device.</summary>
/// <param name="Best">The scenario to report, or null when no scenario is current.</param>
/// <param name="Moved">How many scenarios' locators reported a new position this pass.</param>
public sealed record FusionResult(Scenario? Best, int Moved);

/// <summary>
/// Combines a device's scenarios into one answer. No MQTT, history or events: everything it
/// touches is on the <see cref="Device"/> and its <see cref="Scenario"/>s.
///   1. Runs every scenario's locator.
///   2. Feeds the scenario with the highest raw Confidence into the device-level Kalman filter.
///   3. Weights each scenario's confidence by motion consistency (Gaussian penalty against the
///      Kalman predicted location) and smooths per-scenario probabilities over time.
///   4. Picks the current scenario with the highest smoothed probability; ties go to the higher
///      confidence, then to the earlier scenario.
/// </summary>
public static class ScenarioFusion
{
    /// <param name="fingerprint">When enabled with priority, a current fingerprint fix above min_confidence is reported over ranging.</param>
    public static FusionResult Update(Device device, ConfigFiltering filtering, DateTime now, FingerprintConfig? fingerprint = null)
    {
        device.LastCalculated = now;
        var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

        // Fingerprint fixes come from what the radio actually does at each spot; ranging comes from a model
        // walls break. When the survey covers where the device is, the fingerprint fix is the one to report,
        // and ranging only stands in outside the survey, where no fingerprint scenario is current.
        IEnumerable<Scenario> candidates = device.Scenarios.Where(s => s.Current);
        if (fingerprint is { Enabled: true, Priority: true } &&
            candidates.Any(s => s.IsFingerprint && (s.Confidence ?? 0) >= fingerprint.MinConfidence))
            candidates = candidates.Where(s => s.IsFingerprint);
        var pool = candidates.ToList();

        var kalmanSource = pool
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        if (kalmanSource != null)
            device.UpdateLocation(kalmanSource.Location);

        // Predicted location from the updated Kalman filter (x̂ₖ|ₖ⁻¹)
        var (predictedLocation, _) = device.KalmanFilter.GetPrediction();

        foreach (var scenario in device.Scenarios)
        {
            if (!scenario.Current)
            {
                scenario.WeightedConfidence = 0;
                continue;
            }

            double delta = predictedLocation.DistanceTo(scenario.Location);
            double mcw = Math.Exp(-(delta * delta) / (2 * filtering.MotionSigma * filtering.MotionSigma));
            scenario.WeightedConfidence = (scenario.Confidence ?? 0) * mcw;
        }

        double totalWeightedConfidence = device.Scenarios.Sum(s => s.WeightedConfidence);
        if (totalWeightedConfidence > 0)
        {
            var priorWeight = filtering.SmoothingWeight;
            var newDataWeight = 1.0 - priorWeight;

            foreach (var scenario in device.Scenarios)
            {
                double newProb = scenario.WeightedConfidence / totalWeightedConfidence;
                scenario.Probability = priorWeight * scenario.Probability + newDataWeight * newProb;
            }

            double sumProb = device.Scenarios.Sum(s => s.Probability);
            if (sumProb > 0)
            {
                foreach (var scenario in device.Scenarios)
                    scenario.Probability /= sumProb;
            }
        }

        var best = pool
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault();

        device.BestScenario = best;
        return new FusionResult(best, moved);
    }
}
