using ESPresense.Events;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests;

[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class NodeTelemetryStorePowerTests
{
    private NodeTelemetryStore _store = null!;
    private Func<NodeSettingReceivedEventArgs, Task> _receive = null!;

    [SetUp]
    public async Task Setup()
    {
        var mqtt = new Mock<IMqttCoordinator>();
        var subscribed = new TaskCompletionSource<Func<NodeSettingReceivedEventArgs, Task>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        mqtt.SetupAdd(m => m.NodeSettingReceivedAsync += It.IsAny<Func<NodeSettingReceivedEventArgs, Task>>())
            .Callback<Func<NodeSettingReceivedEventArgs, Task>>(h => subscribed.TrySetResult(h));

        _store = new NodeTelemetryStore(mqtt.Object);
        await _store.StartAsync(CancellationToken.None);
        _receive = await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TearDown]
    public async Task TearDown()
    {
        await _store.StopAsync(CancellationToken.None);
        _store.Dispose();
    }

    private void Receive(string nodeId, string setting, string? payload) =>
        _receive(new NodeSettingReceivedEventArgs { NodeId = nodeId, Setting = setting, Payload = payload })
            .GetAwaiter().GetResult();

    [Test]
    public void Power_IsNull_UntilReported()
    {
        Assert.That(_store.Power("garage"), Is.Null);
    }

    [Test]
    public void Power_TracksLatestReading()
    {
        Receive("master", "power", "3.8");
        Assert.That(_store.Power("master"), Is.EqualTo(3.8).Within(0.001));

        Receive("master", "power", "39.2");
        Assert.That(_store.Power("master"), Is.EqualTo(39.2).Within(0.001));
    }

    [Test]
    public void Power_ZeroIsAReading_NotAbsence()
    {
        // A plug with nothing drawing on it must read 0 W, not "unknown" — that
        // distinction is the whole point of using power to identify a node.
        Receive("garage", "power", "0.0");
        Assert.That(_store.Power("garage"), Is.EqualTo(0.0));
    }

    [Test]
    public void Power_ClearedWhenUnparsable()
    {
        Receive("master", "power", "4.8");
        Receive("master", "power", "");
        Assert.That(_store.Power("master"), Is.Null);
    }

    [Test]
    public void Power_IgnoresOtherSettings()
    {
        Receive("master", "absorption", "3.10");
        Assert.That(_store.Power("master"), Is.Null);
    }
}
