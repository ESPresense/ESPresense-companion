using System;
using System.Collections.Generic;
using System.Linq;
using ESPresense.Locators;

namespace ESPresense.Simulation.AccuracyHarness;

/// <summary>
/// Proves the accuracy baseline covers every locator it claims to.
///
/// The gate this harness feeds triggers on all of <c>src/Locators/**</c>, so it
/// runs on a change to a locator the baseline has no row for — and such a run
/// can only ever be green. That is how MultiFloor and NadarayaWatson came to be
/// gated by a check that could not see them. This turns the "keep LocatorKind in
/// sync with src/Locators/" comment into something CI enforces: add a locator
/// without a baseline row and <c>accuracy check</c> fails, naming it.
/// </summary>
public static class LocatorCoverage
{
    /// <summary>
    /// Locators that are deliberately not in the baseline, by full type name,
    /// with the reason. Neither solves a position from ranges, so a
    /// positioning-error number for them would not measure a solve. Keyed by
    /// name rather than <c>typeof</c> because <c>NearestNode</c> is internal to
    /// ESPresense.Companion.
    /// </summary>
    private static readonly Dictionary<string, string> NotScored = new(StringComparer.Ordinal)
    {
        ["ESPresense.Locators.AnchorLocator"] = "pins a device to a configured point; there is no solve to score",
        ["ESPresense.Locators.NearestNode"] = "reports the nearest node's own position; there is no solve to score"
    };

    /// <summary>
    /// Concrete <see cref="ILocate"/> implementations in ESPresense.Companion that
    /// are neither scored by a <see cref="LocatorKind"/> nor listed in
    /// <see cref="NotScored"/>. Empty is the only passing result.
    /// </summary>
    public static IReadOnlyList<string> FindUncovered()
    {
        var covered = Enum.GetValues<LocatorKind>()
            .Select(AccuracyRunner.LocatorTypeFor)
            .ToHashSet();

        return typeof(ILocate).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILocate).IsAssignableFrom(t))
            .Where(t => !covered.Contains(t))
            .Where(t => t.FullName is null || !NotScored.ContainsKey(t.FullName))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Human-readable explanation for the <c>check</c> failure output.</summary>
    public static IEnumerable<string> DescribeFailure(IReadOnlyList<string> uncovered)
    {
        yield return $"{uncovered.Count} locator(s) in src/Locators/ have no accuracy baseline:";
        foreach (var name in uncovered)
            yield return $"  {name}";
        yield return "";
        yield return "The accuracy gate triggers on src/Locators/**, so it will run on a change to";
        yield return "these and pass regardless of what the change does. Either:";
        yield return "  - add the locator to LocatorKind + AccuracyRunner.BuildContext and regenerate";
        yield return "    the baseline, or";
        yield return "  - if it does not solve a position from ranges, add it to";
        yield return "    LocatorCoverage.NotScored with the reason, and say so in docs/accuracy.md.";
    }
}
