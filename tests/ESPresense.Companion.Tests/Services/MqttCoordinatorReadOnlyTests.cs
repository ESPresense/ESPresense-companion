using ESPresense.Models;
using ESPresense.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Covers the read-only decision of <see cref="MqttCoordinator"/> (explicit mqtt.read_only flag plus the
/// legacy client-id heuristic) and the config plumbing behind it. The connection paths themselves
/// need a broker and are not covered here.
/// </summary>
public class MqttCoordinatorReadOnlyTests
{
    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("espresense-companion", false)]
    [TestCase("bedroom-reader", true)]
    [TestCase("READONLY", true)]
    [TestCase("thread-tracker", true)] // documents the false positive the explicit flag is meant to replace
    public void IsReadOnlyClient_IsTrueForAnyClientIdContainingRead(string? clientId, bool expected)
    {
        Assert.That(MqttCoordinator.IsReadOnlyClient(clientId), Is.EqualTo(expected));
    }

    [Test]
    public void GetReadOnlyReason_DefaultConfigIsNotReadOnly()
    {
        var config = new ConfigMqtt();

        Assert.That(config.ReadOnly, Is.False);
        Assert.That(MqttCoordinator.GetReadOnlyReason(config), Is.EqualTo(MqttCoordinator.ReadOnlyReason.None));
    }

    [Test]
    public void GetReadOnlyReason_ExplicitFlagEnablesReadOnlyRegardlessOfClientId()
    {
        var config = new ConfigMqtt { ClientId = "espresense-companion", ReadOnly = true };

        Assert.That(MqttCoordinator.GetReadOnlyReason(config), Is.EqualTo(MqttCoordinator.ReadOnlyReason.ConfigFlag));
    }

    [Test]
    public void GetReadOnlyReason_ExplicitFlagIsReportedOverHeuristic()
    {
        var config = new ConfigMqtt { ClientId = "bedroom-reader", ReadOnly = true };

        Assert.That(MqttCoordinator.GetReadOnlyReason(config), Is.EqualTo(MqttCoordinator.ReadOnlyReason.ConfigFlag));
    }

    [Test]
    public void GetReadOnlyReason_FallsBackToLegacyClientIdHeuristic()
    {
        var config = new ConfigMqtt { ClientId = "bedroom-reader", ReadOnly = false };

        Assert.That(MqttCoordinator.GetReadOnlyReason(config), Is.EqualTo(MqttCoordinator.ReadOnlyReason.ClientIdHeuristic));
    }

    [Test]
    public void Clone_CopiesReadOnly()
    {
        var config = new ConfigMqtt { Host = "broker", ClientId = "companion", ReadOnly = true };

        var clone = config.Clone();

        Assert.That(clone.ReadOnly, Is.True);
        Assert.That(clone.ClientId, Is.EqualTo("companion"));
    }

    [Test]
    public void ReadOnly_IsParsedFromYaml()
    {
        // Same deserializer settings as ConfigLoader.
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var config = deserializer.Deserialize<Config>(@"
mqtt:
  host: broker
  client_id: companion
  read_only: true
");

        Assert.That(config.Mqtt.ReadOnly, Is.True);
        Assert.That(config.Mqtt.ClientId, Is.EqualTo("companion"));
        Assert.That(MqttCoordinator.GetReadOnlyReason(config.Mqtt), Is.EqualTo(MqttCoordinator.ReadOnlyReason.ConfigFlag));
    }

    [Test]
    public void ReadOnly_DefaultsToFalseWhenAbsentFromYaml()
    {
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var config = deserializer.Deserialize<Config>(@"
mqtt:
  host: broker
");

        Assert.That(config.Mqtt.ReadOnly, Is.False);
        Assert.That(MqttCoordinator.GetReadOnlyReason(config.Mqtt), Is.EqualTo(MqttCoordinator.ReadOnlyReason.None));
    }
}
