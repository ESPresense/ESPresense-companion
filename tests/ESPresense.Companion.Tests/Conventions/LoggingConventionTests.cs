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

    [Test]
    public void StaticSerilogLog_IsOnlyUsedByBootstrapAndDeferredFiles()
    {
        var allowed = new HashSet<string>(Bootstrap.Concat(Deferred), StringComparer.Ordinal);
        var offenders = SourceScan.FilesMatching(StaticSerilog).Where(f => !allowed.Contains(f)).ToList();

        Assert.That(offenders, Is.Empty,
            "Inject ILogger<T> through the constructor instead of calling the static Serilog.Log. Offending files: " + string.Join(", ", offenders));
    }

    [Test]
    public void Scan_RecognisesTheBootstrapUse()
    {
        // Guards the guard: Program.cs really does use Log.Logger, so a scan that finds nothing there is broken.
        Assert.That(SourceScan.FilesMatching(StaticSerilog), Does.Contain("Program.cs"));
    }

    private static readonly Regex DeferredFactory = new(@"\bDeferredLoggerFactory\b", RegexOptions.Compiled);

    // Types constructed by the deferred files above, which can't pass them a logger yet.
    // TODO(#1843, #1844, #1845): empty this list and delete DeferredLoggerFactory once those merge.
    private static readonly string[] DeferredFactoryUsers =
    {
        "Program.cs",
        "Utils/DeferredLoggerFactory.cs",
        "Locators/BaseMultilateralizer.cs",          // built by State (#1844)
        "Locators/MultiFloorMultilateralizer.cs",    // built by State (#1844)
        "Locators/NadarayaWatsonMultilateralizer.cs", // built by State (#1844)
        "Models/AutoDiscovery.cs",                   // built via Device by DeviceTracker/MqttCoordinator (#1843, #1844)
    };

    [Test]
    public void DeferredLoggerFactory_DoesNotSpread()
    {
        var users = SourceScan.FilesMatching(DeferredFactory);
        Assert.That(users, Is.EquivalentTo(DeferredFactoryUsers),
            "DeferredLoggerFactory is only a bridge for types the deferred files construct; inject ILogger<T> everywhere else.");
    }
}
