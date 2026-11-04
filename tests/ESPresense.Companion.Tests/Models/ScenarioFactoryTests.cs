using System.Reflection;
using ESPresense.Models;

namespace ESPresense.Companion.Tests.Models;

public class ScenarioFactoryTests
{
    private static Config AllEnabled() => new()
    {
        Locators = new ConfigLocators
        {
            NelderMead = { Enabled = true }, Bfgs = { Enabled = true }, Mle = { Enabled = true },
            MultiFloor = { Enabled = true }, NadarayaWatson = { Enabled = true }, NearestNode = { Enabled = true },
        }
    };

    [Test]
    public async Task EveryLocatorConfigSection_HasExactlyOneDescriptor()
    {
        await using var fx = await YamlStateFixture.LoadAsync("timeout: 30\n");
        var sections = typeof(ConfigLocators).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        var descriptors = fx.State.ScenarioFactory.All(AllEnabled()).Select(d => d.Name).ToList();

        Assert.That(descriptors, Is.Unique);
        Assert.That(descriptors, Is.EquivalentTo(sections), "a ConfigLocators section without a descriptor is never run, and vice versa");
    }

    [Test]
    public async Task Enabled_FollowsTheConfigFlags()
    {
        await using var fx = await YamlStateFixture.LoadAsync("timeout: 30\n");
        var config = new Config { Locators = new ConfigLocators { Mle = { Enabled = true }, NearestNode = { Enabled = true } } };
        Assert.Multiple(() =>
        {
            Assert.That(fx.State.ScenarioFactory.Enabled(config).Select(d => d.Name), Is.EqualTo(new[] { "Mle", "NearestNode" }));
            Assert.That(fx.State.ScenarioFactory.Enabled(null), Is.Empty);
            Assert.That(fx.State.ScenarioFactory.Enabled(new Config()), Is.Empty);
        });
    }
}
