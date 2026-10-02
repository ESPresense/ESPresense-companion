using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace ESPresense.Companion.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NodeSettingsStoreTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private NodeSettingsStore _store = null!;

    [SetUp]
    public void Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        _mqtt.Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _store = new NodeSettingsStore(_mqtt.Object, Mock.Of<ILogger<NodeSettingsStore>>());

        // Start the background service so it subscribes to NodeSettingReceivedAsync
        var startTask = _store.StartAsync(CancellationToken.None);
        startTask.Wait(TimeSpan.FromSeconds(1));
        Assert.That(startTask.IsCompleted, Is.True, "Service should start within 1 second");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _store.StopAsync(CancellationToken.None);
        _store.Dispose();
    }

    private void Receive(string nodeId, string setting, string payload)
    {
        _mqtt.Raise(m => m.NodeSettingReceivedAsync += null,
            new NodeSettingReceivedEventArgs { NodeId = nodeId, Setting = setting, Payload = payload });
    }

    [Test]
    public void Inbound_ForgetAfterMs_PopulatesScanningSettings()
    {
        Receive("node-1", "forget_after_ms", "150000");

        Assert.That(_store.Get("node-1").Scanning.ForgetAfterMs, Is.EqualTo(150000));
    }

    [Test]
    public void Inbound_CountMs_PopulatesCountingSettings()
    {
        Receive("node-1", "count_ms", "30000");

        Assert.That(_store.Get("node-1").Counting.MinMs, Is.EqualTo(30000));
    }

    [Test]
    public async Task Set_ThenGet_ReturnsNewValuesBeforeBrokerEcho()
    {
        var settings = new NodeSettings("node-1")
        {
            Name = "Kitchen",
            Scanning = { ForgetAfterMs = 150000 },
            Counting = { MinMs = 30000 },
            Calibration = { Absorption = 3.1 }
        };

        await _store.Set("node-1", settings);

        var result = _store.Get("node-1");
        Assert.That(result.Name, Is.EqualTo("Kitchen"));
        Assert.That(result.Scanning.ForgetAfterMs, Is.EqualTo(150000));
        Assert.That(result.Counting.MinMs, Is.EqualTo(30000));
        Assert.That(result.Calibration.Absorption, Is.EqualTo(3.1));

        _mqtt.Verify(x => x.EnqueueAsync("espresense/rooms/node-1/forget_after_ms/set", "150000", false), Times.Once);
        _mqtt.Verify(x => x.EnqueueAsync("espresense/rooms/node-1/count_ms/set", "30000", false), Times.Once);
    }

    [Test]
    public async Task Set_DoesNotResendValuesAlreadyEchoedByBroker()
    {
        Receive("node-1", "forget_after_ms", "150000");
        Receive("node-1", "count_ms", "30000");

        var settings = new NodeSettings("node-1")
        {
            Scanning = { ForgetAfterMs = 150000 },
            Counting = { MinMs = 30000 }
        };

        await _store.Set("node-1", settings);

        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task Set_OnlyPublishesValuesThatChangedSinceLastSet()
    {
        await _store.Set("node-1", new NodeSettings("node-1") { Scanning = { ForgetAfterMs = 150000 }, Counting = { MinMs = 30000 } });
        _mqtt.Invocations.Clear();

        await _store.Set("node-1", new NodeSettings("node-1") { Scanning = { ForgetAfterMs = 150000 }, Counting = { MinMs = 45000 } });

        _mqtt.Verify(x => x.EnqueueAsync("espresense/rooms/node-1/count_ms/set", "45000", false), Times.Once);
        _mqtt.Verify(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task BrokerEcho_StillOverwritesOptimisticCache()
    {
        await _store.Set("node-1", new NodeSettings("node-1") { Scanning = { ForgetAfterMs = 150000 } });
        Assert.That(_store.Get("node-1").Scanning.ForgetAfterMs, Is.EqualTo(150000));

        Receive("node-1", "forget_after_ms", "200000");

        Assert.That(_store.Get("node-1").Scanning.ForgetAfterMs, Is.EqualTo(200000));
    }

    [Test]
    public async Task Set_PreservesValuesNotIncludedInTheUpdate()
    {
        Receive("node-1", "name", "Kitchen");
        Receive("node-1", "absorption", "3.2");

        await _store.Set("node-1", new NodeSettings("node-1") { Counting = { MinMs = 30000 } });

        var result = _store.Get("node-1");
        Assert.That(result.Name, Is.EqualTo("Kitchen"));
        Assert.That(result.Calibration.Absorption, Is.EqualTo(3.2));
        Assert.That(result.Counting.MinMs, Is.EqualTo(30000));
    }

    /// <summary>
    /// Merge is a hand-written field-by-field copy, so a property added to NodeSettings (or to one of its
    /// nested settings classes) would silently stop being cached without any compile error. This drives the
    /// public Set/Get path with every nullable setting populated via reflection: any property Merge forgets
    /// shows up here as a mismatch.
    /// </summary>
    [Test]
    public async Task Set_ThenGet_CachesEveryNullableSettingOnNodeSettings()
    {
        var settings = new NodeSettings("node-1") { Name = "Kitchen" };
        var expected = new Dictionary<string, object>();

        foreach (var section in SettingsSections(settings))
        {
            var sectionValue = section.GetValue(settings)!;
            foreach (var setting in NullableSettings(section.PropertyType))
            {
                // Non-zero values: Merge mirrors UpdateSetting publishing 0 as "", which echoes back as null
                object value = setting.PropertyType == typeof(string) ? "value-" + setting.Name
                    : setting.PropertyType == typeof(int?) ? 11
                    : setting.PropertyType == typeof(double?) ? 1.25
                    : setting.PropertyType == typeof(bool?) ? true
                    : throw new InvalidOperationException($"Unhandled setting type {setting.PropertyType} on {section.Name}.{setting.Name}");

                setting.SetValue(sectionValue, value);
                expected[$"{section.Name}.{setting.Name}"] = value;
            }
        }

        await _store.Set("node-1", settings);
        var result = _store.Get("node-1");

        Assert.Multiple(() =>
        {
            foreach (var (path, value) in expected)
            {
                var dot = path.IndexOf('.');
                var sectionName = path[..dot];
                var settingName = path[(dot + 1)..];
                var actual = SettingsSections(result).Single(p => p.Name == sectionName).GetValue(result)!
                    .GetType().GetProperty(settingName)!.GetValue(SettingsSections(result).Single(p => p.Name == sectionName).GetValue(result));
                Assert.That(actual, Is.EqualTo(value), $"{path} was not merged into the cache");
            }
        });
    }

    /// <summary>The nested settings sections of <see cref="NodeSettings"/> (updating, scanning, counting, ...)</summary>
    private static IEnumerable<PropertyInfo> SettingsSections(NodeSettings settings) =>
        typeof(NodeSettings).GetProperties().Where(p => p.PropertyType.GetMethod("Clone") is not null);

    /// <summary>The settings a <see cref="NodeSettings"/> section can carry (every nullable property)</summary>
    private static IEnumerable<PropertyInfo> NullableSettings(Type sectionType) =>
        sectionType.GetProperties().Where(p => p.CanWrite && (Nullable.GetUnderlyingType(p.PropertyType) is not null || p.PropertyType == typeof(string)));
}
