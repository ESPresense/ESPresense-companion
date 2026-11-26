using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace ESPresense.Companion.Tests;

/// <summary>
/// Keys in config.yaml that no setting reads are reported: the loader ignores them so one typo cannot stop the config
/// loading, which also left a misspelt setting, or one the running version does not have, silently without effect.
/// </summary>
public class ConfigKeyCheckTests
{
    private static string Example()
    {
        using var s = typeof(ConfigLoader).Assembly.GetManifestResourceStream("ESPresense.config.example.yaml")!;
        return new StreamReader(s).ReadToEnd();
    }

    [Test]
    public void TheExampleConfig_HasNoUnknownKeys()
    {
        // Proves the walk matches the real deserializer on every section the example uses.
        Assert.That(ConfigKeyCheck.UnknownKeys<Config>(Example()), Is.Empty);
    }

    [Test]
    public void Typos_AreReportedWithTheirPath_AtEveryLevel()
    {
        const string yaml = """
            timeout: 30
            timout: 31
            optimization:
              enabled: true
              optimiser: per_node_absorption
              limits:
                absorption_min: 2.5
                anything_goes_here: 1
            nodes:
              - name: A
                point: [0, 0, 1]
              - name: B
                pointt: [1, 1, 1]
            floors:
              - id: first
                rooms:
                  - name: Hall
                    colour: red
            """;

        Assert.That(ConfigKeyCheck.UnknownKeys<Config>(yaml), Is.EqualTo(new[]
        {
            "timout", "optimization.optimiser", "nodes[1].pointt", "floors[0].rooms[0].colour"
        }), "keys inside a dictionary (limits) are free-form and not reported");
    }

    [Test]
    public void BrokenYaml_ReportsNothing_TheLoaderReportsTheSyntaxError()
    {
        Assert.That(ConfigKeyCheck.UnknownKeys<Config>("optimization: [unclosed"), Is.Empty);
    }

    [Test]
    public async Task TheLoader_WarnsOncePerUnknownKey_AndStillLoads()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "keycheck", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "timeout: 30\noptimization:\n  enabled: true\n  judgee: spots\n");
        try
        {
            var log = new FakeLogger<ConfigLoader>();
            using var loader = new ConfigLoader(dir, log);
            await loader.LoadAsync();

            Assert.That(loader.Config!.Optimization.Enabled, Is.True, "the rest of the config still loads");
            var warning = log.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
            Assert.That(warning.Message, Does.StartWith("Unknown setting optimization.judgee in "));
            Assert.That(warning.GetStructuredStateValue("Key"), Is.EqualTo("optimization.judgee"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
