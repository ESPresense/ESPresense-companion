using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Logging;
using Moq;

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
}
