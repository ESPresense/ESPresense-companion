using System;
using System.IO;
using YamlDotNet.Serialization;
using ESPresense.Models;

namespace ESPresense.Companion.Tests;

public class ConfigTests
{
    [Test]
    public void TestLocatorsDeserialization()
    {
        string yaml = @"
        locators:
          nadaraya_watson:
            enabled: true
            floors: [""floor1"", ""floor2""]
            kernel:
              algorithm: gaussian
              props:
                bandwidth: 0.5
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
        Assert.NotNull(nadarayaWatson.Kernel);
        Assert.That(nadarayaWatson.Kernel.Algorithm, Is.EqualTo("gaussian"));
        Assert.True(nadarayaWatson.Kernel.Props.ContainsKey("bandwidth"), "Expected 'bandwidth' property in kernel props");
        Assert.That(nadarayaWatson.Kernel.Props["bandwidth"], Is.EqualTo(0.5));

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
