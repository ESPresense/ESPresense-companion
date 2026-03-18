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
        Assert.That(nadarayaWatson.Kernel.Props["bandwidth"], Is.EqualTo(0.5));

        var nelderMead = config.Locators.NelderMead;
        Assert.False(nelderMead.Enabled);
        Assert.That(nelderMead.Floors, Is.EqualTo(new[] { "floor3" }));

        var nearestNode = config.Locators.NearestNode;
        Assert.True(nearestNode.Enabled);
        Assert.That(nearestNode.MaxDistance, Is.EqualTo(10.0));
    }
}