using System;
using System.IO;
using YamlDotNet.Serialization;
using ESPresense.Models;

namespace ESPresense.Companion.Tests;

public class ConfigTests
{
    [Test]
    public void OptimizationTiming_NewKeysOverrideLegacyFallbacks()
    {
        const string yaml = @"
optimization:
  sample_interval_secs: 15
  training_window_mins: 45
  optimization_interval_secs: 600
  interval_secs: 3600
  keep_snapshot_mins: 5
";

        var config = new DeserializerBuilder().Build().Deserialize<Config>(yaml);

        Assert.That(config.Optimization.EffectiveSampleIntervalSecs, Is.EqualTo(15));
        Assert.That(config.Optimization.EffectiveTrainingWindowMins, Is.EqualTo(45));
        Assert.That(config.Optimization.EffectiveOptimizationIntervalSecs, Is.EqualTo(600));
    }

    [Test]
    public void OptimizationTiming_LegacyKeysRemainSupported()
    {
        const string yaml = @"
optimization:
  interval_secs: 3600
  keep_snapshot_mins: 5
";

        var config = new DeserializerBuilder().Build().Deserialize<Config>(yaml);

        Assert.That(config.Optimization.EffectiveSampleIntervalSecs, Is.EqualTo(30));
        Assert.That(config.Optimization.EffectiveTrainingWindowMins, Is.EqualTo(5));
        Assert.That(config.Optimization.EffectiveOptimizationIntervalSecs, Is.EqualTo(3600));
    }

    [Test]
    public void TestLocatorsDeserialization()
    {
        string yaml = @"
        locators:
          nadaraya_watson:
            enabled: true
            floors: [""floor1"", ""floor2""]
            bandwidth: 0.5
            kernel: ""gaussian""
          nelder_mead:
            enabled: false
            floors: [""floor3""]
            weighting:
              algorithm: ""linear""
          bfgs:
            enabled: true
            floors: [""floor4""]
            weighting:
              algorithm: ""gaussian""
              props:
                sigma: 0.30
          mle:
            enabled: true
            floors: [""floor5""]
            weighting:
              algorithm: ""exponential""
          multi_floor:
            enabled: false
            weighting:
              algorithm: ""equal""
          nearest_node:
            enabled: true
            max_distance: 10.0
        ";

        var deserializer = new DeserializerBuilder().Build();
        var config = deserializer.Deserialize<Config>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Locators);

        var nadarayaWatson = config.Locators.NadarayaWatson;
        Assert.True(nadarayaWatson.Enabled);
        Assert.That(nadarayaWatson.Floors, Is.EqualTo(new[] { "floor1", "floor2" }));
        Assert.That(nadarayaWatson.Bandwidth, Is.EqualTo(0.5));
        Assert.That(nadarayaWatson.Kernel, Is.EqualTo("gaussian"));

        var nelderMead = config.Locators.NelderMead;
        Assert.False(nelderMead.Enabled);
        Assert.That(nelderMead.Floors, Is.EqualTo(new[] { "floor3" }));
        Assert.NotNull(nelderMead.Weighting);
        Assert.That(nelderMead.Weighting.Algorithm, Is.EqualTo("linear"));

        var bfgs = config.Locators.Bfgs;
        Assert.True(bfgs.Enabled);
        Assert.That(bfgs.Floors, Is.EqualTo(new[] { "floor4" }));
        Assert.NotNull(bfgs.Weighting);
        Assert.That(bfgs.Weighting.Algorithm, Is.EqualTo("gaussian"));
        Assert.That(bfgs.Weighting.Props["sigma"], Is.EqualTo(0.30));

        var mle = config.Locators.Mle;
        Assert.True(mle.Enabled);
        Assert.That(mle.Floors, Is.EqualTo(new[] { "floor5" }));
        Assert.NotNull(mle.Weighting);
        Assert.That(mle.Weighting.Algorithm, Is.EqualTo("exponential"));

        var multiFloor = config.Locators.MultiFloor;
        Assert.False(multiFloor.Enabled);
        Assert.NotNull(multiFloor.Weighting);
        Assert.That(multiFloor.Weighting.Algorithm, Is.EqualTo("equal"));

        var nearestNode = config.Locators.NearestNode;
        Assert.True(nearestNode.Enabled);
        Assert.That(nearestNode.MaxDistance, Is.EqualTo(10.0));
    }
}
