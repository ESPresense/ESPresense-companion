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
            bandwidth: 0.5
            kernel: ""gaussian""
          nelder_mead:
            enabled: false
            floors: [""floor3""]
          bfgs:
            enabled: true
            floors: [""floor4""]
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

        var bfgs = config.Locators.Bfgs;
        Assert.True(bfgs.Enabled);
        Assert.That(bfgs.Floors, Is.EqualTo(new[] { "floor4" }));

        var nearestNode = config.Locators.NearestNode;
        Assert.True(nearestNode.Enabled);
        Assert.That(nearestNode.MaxDistance, Is.EqualTo(10.0));
    }
}
