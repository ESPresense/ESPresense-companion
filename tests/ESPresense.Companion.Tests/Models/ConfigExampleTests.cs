using ESPresense.Services;

namespace ESPresense.Companion.Tests.Models;

/// <summary>The shipped example configuration must parse, with every documented section landing on its class.</summary>
[TestFixture]
public class ConfigExampleTests
{
    private static string FindExample()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "config.example.yaml"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "src/config.example.yaml not found above the test directory");
        return Path.Combine(dir!.FullName, "src", "config.example.yaml");
    }

    [Test]
    public async Task ExampleConfigParsesSurveyAccuracyAndTracker()
    {
        var configDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfg", Guid.NewGuid().ToString());
        Directory.CreateDirectory(configDir);
        File.Copy(FindExample(), Path.Combine(configDir, "config.yaml"));
        var loader = new ConfigLoader(configDir);
        try
        {
            await loader.LoadAsync(); // main's ConfigLoader loads in ExecuteAsync; a hand-built one must be told to load
            var config = await loader.ConfigAsync();
            Assert.Multiple(() =>
            {
                Assert.That(config.Survey.Robot.Enabled, Is.False);
                Assert.That(config.Survey.Robot.Device, Is.EqualTo("node:calibrate"));
                Assert.That(config.Survey.Robot.Pose.Entity, Is.EqualTo("camera.p10_pro_ultra_map"));
                Assert.That(config.Survey.Robot.Transform.Scale, Is.EqualTo(0.001));
                Assert.That(config.Survey.Robot.MaxFingerprints, Is.EqualTo(20000));
                Assert.That(config.Accuracy.Enabled, Is.True);
                Assert.That(config.Accuracy.HoldoutSpots, Is.EqualTo(40));
                Assert.That(config.Locators.Fingerprint.ReadingWindowSecs, Is.EqualTo(15));
                Assert.That(config.Locators.Fingerprint.Priority, Is.True);
                Assert.That(config.Locators.Fingerprint.Tracker.Enabled, Is.False);
                Assert.That(config.Locators.Fingerprint.Tracker.Temper, Is.EqualTo(0.5));
            });
            var clone = config.Clone();
            Assert.That(clone.Survey.Robot.Device, Is.EqualTo("node:calibrate"));
            Assert.That(clone.Locators.Fingerprint.Tracker.Temper, Is.EqualTo(0.5));
            Assert.That(clone.Accuracy.HoldoutSpots, Is.EqualTo(40));
        }
        finally
        {
            await loader.StopAsync(CancellationToken.None);
            loader.Dispose();
            Directory.Delete(configDir, true);
        }
    }
}
