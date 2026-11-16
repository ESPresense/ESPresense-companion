using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Moq;

namespace ESPresense.Companion.Tests;

[TestFixture]
public class DeviceCaptureServiceTests
{
    private Mock<IMqttCoordinator> _mqtt = null!;
    private DeviceCaptureService _service = null!;

    [SetUp]
    public void Setup()
    {
        _mqtt = new Mock<IMqttCoordinator>();
        var config = new Mock<ConfigLoader>("test-config-dir");
        var state = new State(config.Object, new Mock<NodeTelemetryStore>(_mqtt.Object).Object);
        _service = new DeviceCaptureService(_mqtt.Object, state);
    }

    private void Send(string deviceId) => _mqtt.Raise(
        m => m.DeviceMessageReceivedAsync += null,
        new DeviceMessageEventArgs { DeviceId = deviceId, NodeId = "n1", Payload = new DeviceMessage { Distance = 1, Rssi = -60 } });

    [Test]
    public void Nothing_Is_Recorded_After_Stop_And_Stop_Is_Idempotent()
    {
        _service.Start("d1");
        Send("d1");
        var stopped = _service.Stop("d1")!.Value;
        Send("d1");
        Assert.That(_service.AddPosition("d1", 1, 2, 3, null), Is.Null);
        var again = _service.Stop("d1")!.Value;

        Assert.That(stopped.active, Is.False);
        Assert.That(again, Is.EqualTo(stopped));
        Assert.That(_service.GetStatus("d1")!.Value.count, Is.EqualTo(1));
        Assert.That(_service.GetStatus("d1")!.Value.positions, Is.EqualTo(0));
    }

    [Test]
    public async Task Concurrent_Ingestion_And_Stop_Leaves_Export_Consistent_And_Frozen()
    {
        for (var i = 0; i < 50; i++)
        {
            var id = $"dev{i}";
            _service.Start(id);
            var senders = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (var k = 0; k < 200; k++) Send(id); })).ToArray();
            await Task.Delay(1);
            var stopped = _service.Stop(id)!.Value;
            await Task.WhenAll(senders);

            var export = _service.Export(id, null)!.Value;
            Assert.That(export.messages.Length, Is.EqualTo(stopped.count), "nothing appended after stop returned");
            Assert.That(export.ended, Is.EqualTo(stopped.ended));
        }
    }

    [Test]
    public void Restart_After_Stop_Starts_A_Fresh_Session()
    {
        _service.Start("d1");
        Send("d1");
        _service.Stop("d1");
        var restarted = _service.Start("d1");
        Assert.That(restarted.active, Is.True);
        Assert.That(restarted.count, Is.EqualTo(0));
    }
}
