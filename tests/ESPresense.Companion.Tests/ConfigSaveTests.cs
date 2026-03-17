using ESPresense.Models;
using ESPresense.Services;

namespace ESPresense.Companion.Tests;

public class ConfigSaveTests
{
    private string _tempDir = null!;
    private string _configPath = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "espresense-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.yaml");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Test]
    public async Task SaveSectionAsync_ReplacesOptimizationSection()
    {
        var original = @"mqtt:
  host: localhost
  port: 1883

optimization:
  enabled: false
  interval_secs: 60

locators:
  nearest_node:
    enabled: true
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization
        {
            Enabled = true,
            IntervalSecs = 3600,
            Optimizer = "per_node_absorption"
        });

        var result = await File.ReadAllTextAsync(_configPath);

        // Optimization section was replaced
        Assert.That(result, Does.Contain("enabled: true"));
        Assert.That(result, Does.Contain("interval_secs: 3600"));
        Assert.That(result, Does.Contain("per_node_absorption"));

        // Other sections preserved
        Assert.That(result, Does.Contain("mqtt:"));
        Assert.That(result, Does.Contain("host: localhost"));
        Assert.That(result, Does.Contain("locators:"));
        Assert.That(result, Does.Contain("nearest_node:"));
    }

    [Test]
    public async Task SaveSectionAsync_PreservesCommentsBefore()
    {
        var original = @"# Main MQTT config
mqtt:
  host: localhost

# Optimization settings
optimization:
  enabled: false

# Locator config
locators:
  nearest_node:
    enabled: true
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization { Enabled = true });

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("# Main MQTT config"));
        Assert.That(result, Does.Contain("# Locator config"));
    }

    [Test]
    public async Task SaveSectionAsync_AppendsWhenSectionMissing()
    {
        var original = @"mqtt:
  host: localhost
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("optimization", new ConfigOptimization { Enabled = true });

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("mqtt:"));
        Assert.That(result, Does.Contain("optimization:"));
        Assert.That(result, Does.Contain("enabled: true"));
    }

    [Test]
    public async Task SaveSectionAsync_RejectsProtectedSection()
    {
        await File.WriteAllTextAsync(_configPath, "map:\n  flip_x: false\n");
        var loader = new ConfigLoader(_tempDir);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.SaveSectionAsync("map", new ConfigMap()));
    }

    [Test]
    public async Task SaveSectionAsync_HandlesScalarValues()
    {
        var original = @"timeout: 30
away_timeout: 120
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        await loader.SaveSectionAsync("timeout", 60);

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("timeout: 60"));
        Assert.That(result, Does.Contain("away_timeout: 120"));
    }

    [Test]
    public async Task SaveSectionAsync_RoundTripsOptimizationWithLimits()
    {
        var original = @"optimization:
  enabled: true
  optimizer: legacy
  interval_secs: 60
  limits:
    absorption_min: 2.5
    absorption_max: 3.5
";
        await File.WriteAllTextAsync(_configPath, original);
        var loader = new ConfigLoader(_tempDir);

        var opt = new ConfigOptimization
        {
            Enabled = true,
            Optimizer = "per_node_absorption",
            IntervalSecs = 3600,
            Limits = new Dictionary<string, double>
            {
                { "absorption_min", 2.0 },
                { "absorption_max", 4.0 }
            }
        };

        await loader.SaveSectionAsync("optimization", opt);

        var result = await File.ReadAllTextAsync(_configPath);

        Assert.That(result, Does.Contain("per_node_absorption"));
        Assert.That(result, Does.Contain("interval_secs: 3600"));
        Assert.That(result, Does.Contain("absorption_min: 2"));
        Assert.That(result, Does.Contain("absorption_max: 4"));
    }
}
