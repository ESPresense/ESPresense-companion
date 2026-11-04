using System.Text.RegularExpressions;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// One logging rule (#1846): constructor-injected <c>ILogger&lt;T&gt;</c> everywhere except the
/// <c>Program.cs</c> bootstrap. The static <c>Serilog.Log</c> can't be asserted in tests and logs without
/// the SourceContext that Serilog's per-namespace minimum levels key on.
/// </summary>
[TestFixture]
public class LoggingConventionTests
{
    // Serilog's static entry points: Log.Information(...), Log.Logger, Log.ForContext(...), and so on.
    private static readonly Regex StaticSerilog = new(
        @"\bSerilog\.Log\b|using\s+static\s+Serilog\.Log\b|(?<![\w.])Log\.(Verbose|Debug|Information|Warning|Error|Fatal|Write|ForContext|Logger|CloseAndFlush|IsEnabled|BindMessageTemplate|BindProperty)\b",
        RegexOptions.Compiled);

    private static readonly string[] Bootstrap = { "Program.cs" };

    // TODO(#1843, #1844, #1845): these files are being rewritten on other branches; convert them once those merge.
    private static readonly string[] Deferred =
    {
        "Models/State.cs",                // #1844
        "Services/DeviceTracker.cs",      // #1844
        "Services/MqttCoordinator.cs",    // #1843 (no static Log calls today, but listed so #1843 can't add them unnoticed)
        "Services/MultiScenarioLocator.cs", // #1845
    };

    // Not yet converted. Each conversion commit removes its files from this list.
    private static readonly string[] NotYetConverted =
    {
        "Controllers/NodeController.cs",
        "Controllers/StateController.cs",
        "Extensions/MqttCoordinatorExtensions.cs",
        "Locators/BaseMultilateralizer.cs",
        "Locators/MultiFloorMultilateralizer.cs",
        "Locators/NadarayaWatsonMultilateralizer.cs",
        "Models/AutoDiscovery.cs",
        "Optimizers/AbsorptionErrOptimizer.cs",
        "Optimizers/GlobalAbsorptionRxTxOptimizer.cs",
        "Optimizers/IsotonicRegressionOptimizer.cs",
        "Optimizers/OptimizationRunner.cs",
        "Optimizers/PerNodeAbsorptionRxTx.cs",
        "Optimizers/RxAdjRssiOptimizer.cs",
        "Services/ConfigLoader.cs",
        "Services/DeviceHistoryStore.cs",
        "Services/LeaseService.cs",
        "Services/NodeSettingsStore.cs",
        "Utils/MqttNetLogger.cs",
    };

    [Test]
    public void StaticSerilogLog_IsOnlyUsedByBootstrapAndDeferredFiles()
    {
        var allowed = new HashSet<string>(Bootstrap.Concat(Deferred).Concat(NotYetConverted), StringComparer.Ordinal);
        var offenders = SourceScan.FilesMatching(StaticSerilog).Where(f => !allowed.Contains(f)).ToList();

        Assert.That(offenders, Is.Empty,
            "Inject ILogger<T> through the constructor instead of calling the static Serilog.Log. Offending files: " + string.Join(", ", offenders));
    }

    [Test]
    public void AllowList_HasNoStaleEntries()
    {
        // A file that no longer uses Serilog.Log must come off the list, so it can't quietly regress.
        var users = SourceScan.FilesMatching(StaticSerilog);
        var stale = NotYetConverted.Concat(Bootstrap).Where(f => !users.Contains(f)).ToList();

        Assert.That(stale, Is.Empty, "Remove these from the allow-list; they no longer use Serilog.Log: " + string.Join(", ", stale));
    }
}
