using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using NUnit.Framework;

namespace ESPresense.Companion.Tests;

// Simple in-memory sink to capture log events
public class TestSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent)
    {
        Events.Add(logEvent);
    }
}

[TestFixture]
public class SerilogConfigurationTests
{
    // Helper to build configuration like ASP.NET Core does
    private static IConfiguration BuildConfiguration(Dictionary<string, string>? envVars = null)
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:MinimumLevel:Override:Microsoft"] = "Warning",
                ["Serilog:MinimumLevel:Override:System"] = "Warning"
            });

        // If environment variables provided, add them (they take precedence)
        if (envVars != null)
        {
            builder.AddInMemoryCollection(envVars);
        }

        return builder.Build();
    }

    // Test that reads the config value directly
    private static LogEventLevel ReadConfiguredMinimumLevel(IConfiguration config)
    {
        var levelStr = config["Serilog:MinimumLevel:Default"] ?? "Information";
        return Enum.Parse<LogEventLevel>(levelStr, true);
    }

    [Test]
    public void Default_WithoutEnvironmentVariable_ReturnsInformation()
    {
        var config = BuildConfiguration();
        var level = ReadConfiguredMinimumLevel(config);
        Assert.That(level, Is.EqualTo(LogEventLevel.Information));
    }

    [Test]
    public void WithEnvironmentVariableDebug_ReturnsDebug()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug"
        };
        var config = BuildConfiguration(envVars);
        var level = ReadConfiguredMinimumLevel(config);
        Assert.That(level, Is.EqualTo(LogEventLevel.Debug));
    }

    [Test]
    public void WithEnvironmentVariableVerbose_ReturnsVerbose()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose"
        };
        var config = BuildConfiguration(envVars);
        var level = ReadConfiguredMinimumLevel(config);
        Assert.That(level, Is.EqualTo(LogEventLevel.Verbose));
    }

    [Test]
    public void WithEnvironmentVariableWarning_ReturnsWarning()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Warning"
        };
        var config = BuildConfiguration(envVars);
        var level = ReadConfiguredMinimumLevel(config);
        Assert.That(level, Is.EqualTo(LogEventLevel.Warning));
    }

    [Test]
    public void WithEnvironmentVariableError_ReturnsError()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Error"
        };
        var config = BuildConfiguration(envVars);
        var level = ReadConfiguredMinimumLevel(config);
        Assert.That(level, Is.EqualTo(LogEventLevel.Error));
    }

    [Test]
    public void EnvironmentVariableOverridesAppsettings()
    {
        var baseConfig = BuildConfiguration();
        Assert.That(ReadConfiguredMinimumLevel(baseConfig), Is.EqualTo(LogEventLevel.Information));

        var overrideVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug"
        };
        var overrideConfig = BuildConfiguration(overrideVars);
        Assert.That(ReadConfiguredMinimumLevel(overrideConfig), Is.EqualTo(LogEventLevel.Debug));
    }

    [Test]
    public void OverrideNamespace_WithEnvironmentVariable_AppliesCorrectly()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Override:Microsoft"] = "Debug"
        };
        var config = BuildConfiguration(envVars);
        var microsoftLevel = config["Serilog:MinimumLevel:Override:Microsoft"];
        Assert.That(microsoftLevel, Is.EqualTo("Debug"));
    }

    [Test]
    public void CaseInsensitiveLevelParsing_Works()
    {
        var testCases = new[]
        {
            ("verbose", LogEventLevel.Verbose),
            ("VERBOSE", LogEventLevel.Verbose),
            ("Debug", LogEventLevel.Debug),
            ("DEBUG", LogEventLevel.Debug),
            ("Information", LogEventLevel.Information),
            ("WARNING", LogEventLevel.Warning),
            ("Error", LogEventLevel.Error),
            ("Fatal", LogEventLevel.Fatal)
        };

        foreach (var (input, expected) in testCases)
        {
            var envVars = new Dictionary<string, string>
            {
                ["Serilog:MinimumLevel:Default"] = input
            };
            var config = BuildConfiguration(envVars);
            var level = ReadConfiguredMinimumLevel(config);
            Assert.That(level, Is.EqualTo(expected), $"Failed for input '{input}'");
        }
    }

    [Test]
    public void InvalidLevel_ThrowsException()
    {
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "InvalidLevel"
        };
        var config = BuildConfiguration(envVars);
        Assert.Throws<System.ArgumentException>(() => ReadConfiguredMinimumLevel(config));
    }

    // Behavioral test: Verify that the environment variable actually affects log output
    [Test]
    public void Logger_WithDebugLevel_WritesDebugMessages()
    {
        // Arrange: config with Debug level
        var envVars = new Dictionary<string, string>
        {
            ["Serilog:MinimumLevel:Default"] = "Debug"
        };
        var config = BuildConfiguration(envVars);

        var sink = new TestSink();
        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .WriteTo.Sink(sink);

        using var logger = loggerConfig.CreateLogger();

        // Act
        logger.Debug("Debug message");
        logger.Information("Info message");

        // Assert: Both should be captured because min level is Debug
        Assert.That(sink.Events.Count, Is.EqualTo(2));
        Assert.That(sink.Events.Any(e => e.Level == LogEventLevel.Debug && e.RenderMessage() == "Debug message"));
        Assert.That(sink.Events.Any(e => e.Level == LogEventLevel.Information && e.RenderMessage() == "Info message"));
    }

    [Test]
    public void Logger_WithInformationLevel_FiltersDebugMessages()
    {
        // Arrange: config with Information level (default)
        var config = BuildConfiguration(); // default is Information

        var sink = new TestSink();
        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .WriteTo.Sink(sink);

        using var logger = loggerConfig.CreateLogger();

        // Act
        logger.Debug("Debug message");
        logger.Information("Info message");

        // Assert: Only Info should be captured
        Assert.That(sink.Events.Count, Is.EqualTo(1));
        Assert.That(sink.Events[0].Level, Is.EqualTo(LogEventLevel.Information));
        Assert.That(sink.Events[0].RenderMessage(), Is.EqualTo("Info message"));
    }
}
