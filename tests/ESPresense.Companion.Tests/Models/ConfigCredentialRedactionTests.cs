using System.Text.Json;
using ESPresense.Models;
using Newtonsoft.Json;
using NUnit.Framework;
using StjJsonSerializer = System.Text.Json.JsonSerializer;


namespace ESPresense.Companion.Tests.Models;

/// <summary>
/// The MQTT broker password must never leave the process via any JSON surface
/// (REST controller, MCP resources, logs). See the credential-exposure fix.
/// </summary>
public class ConfigCredentialRedactionTests
{
    private static Config BuildConfig()
    {
        return new Config
        {
            Mqtt = new ConfigMqtt
            {
                Host = "broker.local",
                Port = 1883,
                Username = "mqtt-user",
                Password = "super-secret",
                ClientId = "espresense-companion",
                DiscoveryTopic = "homeassistant"
            }
        };
    }

    [Test]
    public void Password_IsNotSerialized_BySystemTextJson()
    {
        var json = StjJsonSerializer.Serialize(BuildConfig());

        Assert.That(json, Does.Not.Contain("super-secret"), "STJ serialization leaked the password");
        Assert.That(json, Does.Not.Contain("password"), "STJ serialization emitted a password property");
    }

    [Test]
    public void Password_IsNotSerialized_ByNewtonsoft()
    {
        var json = JsonConvert.SerializeObject(BuildConfig());

        Assert.That(json, Does.Not.Contain("super-secret"), "Newtonsoft serialization leaked the password");
        Assert.That(json, Does.Not.Contain("password"), "Newtonsoft serialization emitted a password property");
    }

    [Test]
    public void NonSecretMqttFields_AreStillSerialized()
    {
        // The redaction must be surgical: clients still need host/port/username.
        var json = StjJsonSerializer.Serialize(BuildConfig());

        Assert.That(json, Does.Contain("broker.local"));
        Assert.That(json, Does.Contain("mqtt-user"));
    }

    [Test]
    public void PasswordKey_IsAbsent_CaseInsensitively()
    {
        // Guards against a future naming-convention change re-introducing the key.
        Assert.That(StjJsonSerializer.Serialize(BuildConfig()),
            Does.Not.Contain("password").IgnoreCase);
        Assert.That(JsonConvert.SerializeObject(BuildConfig()),
            Does.Not.Contain("password").IgnoreCase);
    }

    [Test]
    public void Password_IsPreservedInMemory_ForMqttConnect()
    {
        // Redaction is serialization-only; the in-memory value must survive so the
        // broker connection (and Clone()) keep working.
        var config = BuildConfig();

        Assert.That(config.Mqtt.Password, Is.EqualTo("super-secret"));
        Assert.That(config.Clone().Mqtt.Password, Is.EqualTo("super-secret"));
    }
}
