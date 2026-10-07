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
    public void Password_IsNotSerialized_ByNewtonsoft_WithAppSerializerSettings()
    {
        // Other parts of the app serialize via ESPresense.Utils.SerializerSettings.NullIgnore
        // (a camelCase contract resolver). Pipe Config through that exact settings object
        // to prove [JsonIgnore] wins over the contract resolver too.
        var json = JsonConvert.SerializeObject(BuildConfig(), ESPresense.Utils.SerializerSettings.NullIgnore);

        Assert.That(json, Does.Not.Contain("super-secret"), "NullIgnore settings leaked the password");
        Assert.That(json, Does.Not.Contain("password").IgnoreCase);
        Assert.That(json, Does.Contain("broker.local"));
    }

    [Test]
    public void Password_SurvivesFullConfigYamlRoundTrip()
    {
        // YAML is how config.yaml is read and (for a whole Config) how it could be
        // re-written. [JsonIgnore] must not affect YamlDotNet either way.
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = serializer.Serialize(BuildConfig());
        Assert.That(yaml, Does.Contain("password: super-secret"),
            "YAML serialization must retain the password so a round-trip does not drop it");

        var back = deserializer.Deserialize<Config>(yaml);
        Assert.That(back.Mqtt.Password, Is.EqualTo("super-secret"));
        Assert.That(back.Mqtt.Host, Is.EqualTo("broker.local"));
        Assert.That(back.Mqtt.Username, Is.EqualTo("mqtt-user"));
    }

    [Test]
    public async Task ConfigLoader_ReloadAfterEdit_UpdatesPasswordInMemory()
    {
        // End-to-end: the password must load from disk and follow subsequent edits via
        // the hosted background reload loop, proving redaction never touches the load path.
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cfgload", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.yaml");

        ESPresense.Services.ConfigLoader? loader = null;
        try
        {
            // Write the file first: ConfigLoader's constructor kicks off an immediate
            // Load(), which would otherwise race with this write.
            await WriteSharedAsync(path,
                "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: first-secret\n");

            loader = new ESPresense.Services.ConfigLoader(dir);
            await loader.StartAsync(CancellationToken.None);
            await loader.ConfigAsync();
            Assert.That(loader.Config!.Mqtt.Password, Is.EqualTo("first-secret"));

            // Rewrite with a bumped password and wait for the background loop to reload.
            // Write with FileShare.ReadWrite: the background loader may be reading the
            // file concurrently and holds FileShare.Read, which blocks a plain write.
            await WriteSharedAsync(path,
                "mqtt:\n  host: broker.local\n  username: mqtt-user\n  password: second-secret\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (loader.Config!.Mqtt.Password != "second-secret" && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.That(loader.Config!.Mqtt.Password, Is.EqualTo("second-secret"),
                "reloading config.yaml must pick up the new password");
            Assert.That(loader.Config.Mqtt.Host, Is.EqualTo("broker.local"));
        }
        finally
        {
            if (loader != null)
            {
                await loader.StopAsync(CancellationToken.None);
                loader.Dispose();
            }
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void Clone_PreservesMqttPassword()
    {
        // ConfigMqtt.Clone() is hand-written (not JSON-based). MqttCoordinator relies on
        // it for the "did config change?" comparison, so the password must survive the
        // copy even though it is now [JsonIgnore]d.
        var original = BuildConfig();
        original.Mqtt.Password = "clone-secret";

        var clone = original.Clone();

        Assert.That(clone.Mqtt.Password, Is.EqualTo("clone-secret"),
            "cloning must retain the password so reconnect change-detection still works");
        Assert.Multiple(() =>
        {
            Assert.That(clone.Mqtt.Host, Is.EqualTo(original.Mqtt.Host));
            Assert.That(clone.Mqtt.Username, Is.EqualTo(original.Mqtt.Username));
            Assert.That(clone.Mqtt.Port, Is.EqualTo(original.Mqtt.Port));
        });
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

    private static async Task WriteSharedAsync(string path, string contents)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite, 4096, useAsync: true);
                await using var sw = new StreamWriter(fs);
                await sw.WriteAsync(contents);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(50);
            }
        }
    }
}
