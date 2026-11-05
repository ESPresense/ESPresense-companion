using ESPresense.Locators;
using ESPresense.Services;

namespace ESPresense.Models;

/// <summary>
/// One locator the config can enable.
/// </summary>
/// <param name="Name">Locator name, as shown in the locator status. Also the scenario name when not <paramref name="PerFloor"/>.</param>
/// <param name="Enabled">Whether the config enables it.</param>
/// <param name="PerFloor">One scenario per floor (named after the floor), rather than one scenario named <paramref name="Name"/>.</param>
/// <param name="Floors">Floor ids to restrict a per-floor locator to, in order; null means every floor.</param>
/// <param name="Create">Builds the locator for a device; the floor is null unless <paramref name="PerFloor"/>.</param>
public sealed record LocatorDescriptor(string Name, bool Enabled, bool PerFloor, string[]? Floors, Func<Device, Floor?, State, ILocate> Create);

/// <summary>
/// The single source of truth for which locators exist, which the config enables, and the scenarios a device gets.
/// </summary>
public sealed class ScenarioFactory(State state, NodeTelemetryStore nts, ILoggerFactory loggers)
{
    private readonly ILogger _logger = loggers.CreateLogger<ScenarioFactory>();

    // One logger per locator type, shared by every scenario built from it.
    private readonly ILogger<NelderMeadMultilateralizer> _nelderMeadLogger = loggers.CreateLogger<NelderMeadMultilateralizer>();
    private readonly ILogger<BfgsMultilateralizer> _bfgsLogger = loggers.CreateLogger<BfgsMultilateralizer>();
    private readonly ILogger<MLEMultilateralizer> _mleLogger = loggers.CreateLogger<MLEMultilateralizer>();
    private readonly ILogger<MultiFloorMultilateralizer> _multiFloorLogger = loggers.CreateLogger<MultiFloorMultilateralizer>();
    private readonly ILogger<NadarayaWatsonMultilateralizer> _nadarayaWatsonLogger = loggers.CreateLogger<NadarayaWatsonMultilateralizer>();

    /// <summary>
    /// Every locator, in scenario order. The order is load-bearing: MultiScenarioLocator breaks
    /// probability/confidence ties by scenario index.
    /// </summary>
    public IReadOnlyList<LocatorDescriptor> All(Config? c)
    {
        var l = c?.Locators;
        return
        [
            NelderMead(l?.NelderMead?.Enabled ?? false, l?.NelderMead?.Floors),
            new("Bfgs", l?.Bfgs?.Enabled ?? false, true, l?.Bfgs?.Floors, (d, f, s) => new BfgsMultilateralizer(d, f!, s, _bfgsLogger)),
            new("Mle", l?.Mle?.Enabled ?? false, true, l?.Mle?.Floors, (d, f, s) => new MLEMultilateralizer(d, f!, s, _mleLogger)),
            new("MultiFloor", l?.MultiFloor?.Enabled ?? false, false, null, (d, _, s) => new MultiFloorMultilateralizer(d, s, _multiFloorLogger)),
            new("NadarayaWatson", l?.NadarayaWatson?.Enabled ?? false, true, l?.NadarayaWatson?.Floors, (d, f, s) => new NadarayaWatsonMultilateralizer(d, f!, s, nts, _nadarayaWatsonLogger)),
            NearestNodeLocator(l?.NearestNode?.Enabled ?? false),
        ];
    }

    // These two are also the nothing-enabled fallback, so they are built directly rather than looked up by name.
    private LocatorDescriptor NelderMead(bool enabled, string[]? floors) =>
        new("NelderMead", enabled, true, floors, (d, f, s) => new NelderMeadMultilateralizer(d, f!, s, _nelderMeadLogger));

    private static LocatorDescriptor NearestNodeLocator(bool enabled) =>
        new("NearestNode", enabled, false, null, (d, _, s) => new NearestNode(d, s));

    /// <summary>The locators <paramref name="c"/> enables, in scenario order.</summary>
    public IReadOnlyList<LocatorDescriptor> Enabled(Config? c) => All(c).Where(d => d.Enabled).ToList();

    /// <summary>The scenarios to locate <paramref name="device"/> with under the current config.</summary>
    public IEnumerable<Scenario> Create(Device device)
    {
        var config = state.Config;

        // An anchored device is where its anchor says; the locators don't run.
        if (device.IsAnchored && device.Anchor is { } anchor)
        {
            yield return new Scenario(config, new AnchorLocator(anchor.Location, anchor.Floor, anchor.Room, state.Floors.Values), "Anchored")
            {
                Confidence = 100
            };
            yield break;
        }

        var enabled = Enabled(config);
        if (enabled.Count == 0)
        {
            _logger.LogWarning("No locators enabled, using default NelderMead");
            enabled = state.Floors.Values.Any()
                ? [NelderMead(true, floors: null)]
                : [NearestNodeLocator(true)];
        }

        foreach (var locator in enabled)
        {
            if (locator.PerFloor)
                foreach (var floor in state.GetFloorsByIds(locator.Floors))
                    yield return new Scenario(config, locator.Create(device, floor, state), floor.Name);
            else
                yield return new Scenario(config, locator.Create(device, null, state), locator.Name);
        }
    }
}
