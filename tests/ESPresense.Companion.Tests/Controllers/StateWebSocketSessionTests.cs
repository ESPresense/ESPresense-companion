using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ESPresense.Companion.Tests.Controllers;

public class StateWebSocketSessionTests
{
    [Test]
    public async Task IdlePeerCloseEndsSessionAndUnsubscribes()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var session = Create(fixture, events, CancellationToken.None);
        var run = session.RunAsync(socket, false, CancellationToken.None);
        Assert.That(await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)), Does.Contain("\"type\":\"time\""));
        socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), []));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        events.OnDeviceRemoved("after-close");
        Assert.That(socket.Sent.Reader.TryRead(out _), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RequestOrHostCancellationEndsIdleSession(bool hostStop)
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var socket = new TestSocket();
        var session = Create(fixture, new GlobalEventDispatcher(), hostStop ? cancel.Token : CancellationToken.None);
        var run = session.RunAsync(socket, false, hostStop ? CancellationToken.None : cancel.Token);
        await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task SlowClientOverflowEndsSession()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket { BlockSends = true };
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        for (var i = 0; i <= StateWebSocketSession.OutboundCapacity; i++) events.OnDeviceRemoved(i.ToString());
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task OversizedFragmentedCommandEndsSession()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var socket = new TestSocket();
        var run = Create(fixture, new GlobalEventDispatcher(), CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        for (var i = 0; i <= StateWebSocketSession.MaxCommandBytes / 4096; i++)
            socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false), new byte[4096]));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SocketFaultEndsSession(bool sendFault)
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var socket = new TestSocket { FailSends = sendFault, FailReceives = !sendFault };
        var run = Create(fixture, new GlobalEventDispatcher(), CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task EventEnvelopesKeepTheirShape()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        Assert.That(await Next(socket), Does.Match("^\\{\"type\":\"time\",\"data\":\\d+(\\.\\d+)?\\}$"));

        events.OnDeviceRemoved("dev-1");
        Assert.That(await Next(socket), Is.EqualTo("{\"type\":\"deviceRemoved\",\"deviceId\":\"dev-1\"}"));
        events.OnDeviceChanged(Tracked("dev-2"), false);
        var changed = await Next(socket);
        Assert.That(changed, Does.StartWith("{\"type\":\"deviceChanged\",\"data\":{"));
        Assert.That(changed, Does.Contain("\"id\":\"dev-2\""));
        await Close(socket, run);
    }

    [Test]
    public async Task UntrackedDevicesFollowShowAllFilter()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);

        events.OnDeviceChanged(Untracked("hidden"), false);
        events.OnDeviceChanged(Untracked("track-flip"), true);
        Assert.That(await Next(socket), Does.Contain("\"id\":\"track-flip\""), "a track change is always sent");
        await AssertNothingQueued(socket, events);

        await Command(socket, "{\"command\":\"changeFilter\",\"type\":\"showAll\",\"value\":\"true\"}");
        events.OnDeviceChanged(Untracked("shown"), false);
        Assert.That(await Next(socket), Does.Contain("\"id\":\"shown\""));

        await Command(socket, "{\"command\":\"changeFilter\",\"type\":\"showAll\",\"value\":\"false\"}");
        events.OnDeviceChanged(Untracked("hidden-again"), false);
        await AssertNothingQueued(socket, events);
        await Close(socket, run);
    }

    [Test]
    public async Task ShowAllQueryParameterSendsUntrackedDevices()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, true, CancellationToken.None);
        await Next(socket);
        events.OnDeviceChanged(Untracked("untracked"), false);
        Assert.That(await Next(socket), Does.Contain("\"id\":\"untracked\""));
        await Close(socket, run);
    }

    [Test]
    public async Task IncludeFilterSendsOneUntrackedDeviceUntilCleared()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);

        await Command(socket, "{\"command\":\"changeFilter\",\"type\":\"include\",\"value\":\"Open-Device\"}");
        events.OnDeviceChanged(Untracked("other"), false);
        events.OnDeviceChanged(Untracked("open-device"), false);
        Assert.That(await Next(socket), Does.Contain("\"id\":\"open-device\""), "include matches case-insensitively and skips others");

        await Command(socket, "{\"command\":\"changeFilter\",\"type\":\"include\",\"value\":\"\"}");
        events.OnDeviceChanged(Untracked("open-device"), false);
        await AssertNothingQueued(socket, events);
        await Close(socket, run);
    }

    [Test]
    public async Task DeviceMessagesFollowSubscriptions()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);

        events.OnDeviceMessageReceived(Message("dev-1"));
        await AssertNothingQueued(socket, events);

        await Command(socket, "{\"command\":\"subscribe\",\"type\":\"deviceMessage\",\"value\":\"dev-1\"}");
        events.OnDeviceMessageReceived(Message("dev-2"));
        events.OnDeviceMessageReceived(Message("dev-1"));
        var message = await Next(socket);
        Assert.That(message, Does.StartWith("{\"type\":\"deviceMessage\",\"deviceId\":\"dev-1\",\"nodeId\":\"node-1\",\"data\":{"));

        await Command(socket, "{\"command\":\"unsubscribe\",\"type\":\"deviceMessage\",\"value\":\"dev-1\"}");
        events.OnDeviceMessageReceived(Message("dev-1"));
        await AssertNothingQueued(socket, events);
        await Close(socket, run);
    }

    [TestCase("not json")]
    [TestCase("{\"command\":")]
    [TestCase("[1,2]")]
    [TestCase("{\"command\":\"unknown\",\"type\":\"showAll\",\"value\":\"true\"}")]
    public async Task MalformedOrUnknownCommandIsIgnored(string json)
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);

        await Command(socket, json);
        Assert.That(run.IsCompleted, Is.False, "the session stays open");
        events.OnDeviceChanged(Untracked("untracked"), false);
        await AssertNothingQueued(socket, events);

        await Command(socket, "{\"command\":\"changeFilter\",\"type\":\"showAll\",\"value\":\"true\"}");
        events.OnDeviceChanged(Untracked("untracked"), false);
        Assert.That(await Next(socket), Does.Contain("\"id\":\"untracked\""), "later commands still apply");
        await Close(socket, run);
    }

    private static Device Untracked(string id) => new(id, null, TimeSpan.FromMinutes(5));
    private static Device Tracked(string id) => new(id, null, TimeSpan.FromMinutes(5)) { Track = true };
    private static DeviceMessageEventArgs Message(string deviceId) =>
        new() { DeviceId = deviceId, NodeId = "node-1", Payload = new DeviceMessage { Distance = 1, Rssi = -60 } };

    private static Task<string> Next(TestSocket socket) => socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

    /// <summary>Events are queued in order, so a marker arriving next proves nothing was queued before it.</summary>
    private static async Task AssertNothingQueued(TestSocket socket, GlobalEventDispatcher events)
    {
        events.OnDeviceRemoved("marker");
        Assert.That(await Next(socket), Does.Contain("\"deviceId\":\"marker\""));
    }

    /// <summary>Sends a text command and waits until the receive loop asks for the next frame, i.e. has applied it.</summary>
    private static async Task Command(TestSocket socket, string json)
    {
        while (socket.Reads.Reader.TryRead(out _)) { }
        socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(Encoding.UTF8.GetByteCount(json), WebSocketMessageType.Text, true), Encoding.UTF8.GetBytes(json)));
        await socket.Reads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task Close(TestSocket socket, Task run)
    {
        socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), []));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static StateWebSocketSession Create(CalibrationFixture f, GlobalEventDispatcher events, CancellationToken stopping)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(x => x.ApplicationStopping).Returns(stopping);
        return new StateWebSocketSession(f.ConfigLoader, events, new DeviceEnricher(f.DeviceSettings), lifetime.Object, Mock.Of<ILogger<StateWebSocketSession>>());
    }

    private sealed class TestSocket : WebSocket
    {
        public readonly Channel<(WebSocketReceiveResult Result, byte[] Data)> Incoming = Channel.CreateUnbounded<(WebSocketReceiveResult, byte[])>();
        public readonly Channel<int> Reads = Channel.CreateUnbounded<int>();
        public readonly Channel<string> Sent = Channel.CreateUnbounded<string>();
        public bool BlockSends;
        public bool FailSends;
        public bool FailReceives;
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => CloseOutputAsync(status, description, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override void Dispose() { }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (FailReceives) throw new WebSocketException("receive fault");
            Reads.Writer.TryWrite(0);
            var (result, data) = await Incoming.Reader.ReadAsync(token);
            data.CopyTo(buffer.Array!, buffer.Offset);
            if (result.MessageType == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
            return result;
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            if (FailSends) throw new WebSocketException("send fault");
            if (BlockSends) await Task.Delay(Timeout.Infinite, token);
            await Sent.Writer.WriteAsync(Encoding.UTF8.GetString(buffer), token);
        }
    }
}
