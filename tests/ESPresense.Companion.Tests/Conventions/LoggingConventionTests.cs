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

    [Test]
    public void StaticSerilogLog_IsOnlyUsedByBootstrap()
    {
        var allowed = new HashSet<string>(Bootstrap, StringComparer.Ordinal);
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

    // The first string literal after a Microsoft.Extensions.Logging call (optionally preceded by an exception argument).
    private static readonly Regex MelTemplate = new(
        @"\.Log(Trace|Debug|Information|Warning|Error|Critical)\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*,\s*)?""(?<template>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    [Test]
    public void MelTemplates_UseNamedPlaceholders()
    {
        // Microsoft.Extensions.Logging binds placeholders to arguments by position, so "{1} {0}" takes the first
        // argument for {1}; Serilog's Log.* bound {N} by index. Named placeholders can't be misread either way,
        // and are what the Serilog sink stores as properties.
        var offenders = SourceScan.BackendFiles()
            .SelectMany(f => MelTemplate.Matches(f.Text).Select(m => (f.Path, Template: m.Groups["template"].Value)))
            .Where(t => Regex.IsMatch(t.Template, @"\{\d"))
            .Select(t => $"{t.Path}: \"{t.Template}\"")
            .ToList();

        Assert.That(offenders, Is.Empty, "Use named placeholders ({NodeId}) in logging templates: " + string.Join("; ", offenders));
    }
}
