using System;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using ESPresense.Models;
using ESPresense.Converters;
using ESPresense.Services;

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

        var nearestNode = config.Locators.NearestNode;
        Assert.True(nearestNode.Enabled);
        Assert.That(nearestNode.MaxDistance, Is.EqualTo(10.0));
    }

    [Test]
    public void SecretsAreResolved()
    {
        string yaml = @"
mqtt:
  password: !secret mqtt_password
";

        var secrets = new Dictionary<string, string> { ["mqtt_password"] = "1234" };
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var method = typeof(ConfigLoader).GetMethod("ReplaceSecrets", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        yaml = (string)method.Invoke(null, new object[] { yaml, secrets })!;
        var config = deserializer.Deserialize<Config>(yaml);

        Assert.That(config.Mqtt.Password, Is.EqualTo("1234"));
    }
}