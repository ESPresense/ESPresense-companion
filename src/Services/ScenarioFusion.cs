using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>What one fusion pass chose for a device.</summary>
/// <param name="Best">The scenario to report, or null when no scenario is current.</param>
/// <param name="Moved">How many eligible scenarios' locators reported a new position this pass.</param>
public sealed record FusionResult(Scenario? Best, int Moved);

/// <summary>Fuses eligible scenarios into one answer. Behavior and rationale: specs/fusion.md.</summary>
public static class ScenarioFusion
{
    public static FusionResult Update(Device device, ConfigFiltering filtering, DateTime now)
    {
        device.LastCalculated = now;
        var located = device.Scenarios.AsParallel()
            .Select(s => (Scenario: s, Moved: s.Locate()))
            .ToArray();

        var current = device.Scenarios.Where(s => s.Current).ToList();
        var candidates = current.Where(s => !s.IsNearestNode && s.Confidence > 0).ToList();
        // Eligibility precedes probability smoothing.
        if (candidates.Count == 0)
            candidates = current.Where(s => s.IsNearestNode && s.Confidence > 0).ToList();
        if (candidates.Count == 0)
            candidates = current.Where(s => !s.IsNearestNode).ToList();
        var eligible = candidates.ToHashSet();
        var moved = located.Count(s => s.Moved && eligible.Contains(s.Scenario));

        var kalmanSource = candidates
            .OrderByDescending(s => s.Confidence)
            .FirstOrDefault();

        if (kalmanSource != null)
            device.UpdateLocation(kalmanSource.Location);

        // Predicted location from the updated Kalman filter (x̂ₖ|ₖ⁻¹)
        var (predictedLocation, _) = device.KalmanFilter.GetPrediction();

        foreach (var scenario in device.Scenarios)
        {
            if (!eligible.Contains(scenario))
            {
                scenario.WeightedConfidence = 0;
                scenario.Probability = 0;
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
            else
            {
                // A newly eligible tier can have no prior, even with SmoothingWeight == 1.
                foreach (var scenario in candidates)
                    scenario.Probability = scenario.WeightedConfidence / totalWeightedConfidence;
            }
        }

        var best = candidates
            .OrderByDescending(s => s.Probability)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => device.Scenarios.IndexOf(s))
            .FirstOrDefault();

        device.BestScenario = best;
        return new FusionResult(best, moved);
    }
}
