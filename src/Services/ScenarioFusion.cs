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
///   2. Feeds the raw fit of the scenario with the highest raw Confidence, found this pass, into the device-level Kalman filter.
///   3. Weights each scenario's confidence by motion consistency (Gaussian penalty against the
///      Kalman predicted location) and smooths per-scenario probabilities over time.
///   4. Picks the current scenario with the highest smoothed probability; ties go to the higher
///      confidence, then to the earlier scenario.
///   5. Holds the reported floor and room (<see cref="LocationHold"/>) until a challenger earns the switch.
/// </summary>
public static class ScenarioFusion
{
    public static FusionResult Update(Device device, ConfigFiltering filtering, DateTime now)
    {
        device.LastCalculated = now;
        var moved = device.Scenarios.AsParallel().Count(s => s.Locate());

        // Only a fix found on this pass: a scenario stays Current while one node still hears the device, and
        // feeding its old fit again would count it twice and keep the filter from starting over after a gap.
        var kalmanSource = device.Scenarios
            .Where(s => s.Current && s.FitAt >= now)
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        // The raw fit: the scenario's Location is already Kalman-smoothed, and smoothing it twice doubles the lag
        if (kalmanSource != null)
            device.UpdateLocation(kalmanSource.Fit);

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

        var best = device.Scenarios
            .Where(s => s.Current)
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault();

        device.BestScenario = best;

        // Hold the reported floor and room until a challenger earns the switch; away starts over
        if (best != null)
            device.Hold.Update(best.Floor, best.Room, best.Location, now, filtering);
        else
            device.Hold.Reset();

        return new FusionResult(best, moved);
    }
}
