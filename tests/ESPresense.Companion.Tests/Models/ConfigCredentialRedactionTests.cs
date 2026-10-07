using System.Text.Json;
using ESPresense.Models;
using Newtonsoft.Json;
using NUnit.Framework;
using StjJsonSerializer = System.Text.Json.JsonSerializer;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;


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

    [Test]
    public void Password_IsNotSerialized_WithMvcDefaultOptions()
    {
        // ASP.NET Core MVC serializes action results with JsonSerializerDefaults.Web
        // (camelCase). This mirrors the exact options the /api/state/config and MCP
        // endpoints use, proving the response body is redacted in production shape.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var json = StjJsonSerializer.Serialize(BuildConfig(), options);

        Assert.That(json, Does.Not.Contain("super-secret"));
        Assert.That(json, Does.Not.Contain("password").IgnoreCase);
        // Sanity: the camelCased shape is what we think it is.
        Assert.That(json, Does.Contain("broker.local"));
    }

    [Test]
    public void Password_StillLoadsFromYaml()
    {
        // The JSON ignore attributes must not affect YamlDotNet, which is how the
        // password actually arrives from config.yaml at startup.
        const string yaml = "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: yaml-secret\n";
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var config = deserializer.Deserialize<Config>(yaml);

        Assert.That(config.Mqtt.Host, Is.EqualTo("broker.local"));
        Assert.That(config.Mqtt.Password, Is.EqualTo("yaml-secret"),
            "YAML deserialization must still populate the password for the broker connection");
    }

    [Test]
    public async Task SaveSection_LeavesMqttPasswordInFile()
    {
        // SaveSectionAsync only ever writes the section it's given (e.g. optimization).
        // Writing a section must not disturb the mqtt block, password included.
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfgsave", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.yaml");
        await File.WriteAllTextAsync(path,
            "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: persist-secret\noptimization:\n  enabled: false\n");

        try
        {
            var loader = new ESPresense.Services.ConfigLoader(dir);
            await loader.ConfigAsync();
            await loader.SaveSectionAsync("optimization", new ConfigOptimization { Enabled = true });

            var text = await File.ReadAllTextAsync(path);
            Assert.That(text, Does.Contain("persist-secret"), "saving a section dropped the mqtt password");
            Assert.That(text, Does.Contain("broker.local"));

            await loader.StopAsync(CancellationToken.None);
            loader.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
