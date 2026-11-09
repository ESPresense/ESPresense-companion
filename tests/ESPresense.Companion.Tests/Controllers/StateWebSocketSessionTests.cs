using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using ESPresense.Controllers;
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
        socket.Incoming.Writer.TryWrite(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
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
            socket.Incoming.Writer.TryWrite(new WebSocketReceiveResult(4096, WebSocketMessageType.Text, false));
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

    private static StateWebSocketSession Create(CalibrationFixture f, GlobalEventDispatcher events, CancellationToken stopping)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(x => x.ApplicationStopping).Returns(stopping);
        return new StateWebSocketSession(f.ConfigLoader, events, new DeviceEnricher(f.DeviceSettings), lifetime.Object, Mock.Of<ILogger<StateWebSocketSession>>());
    }

    private sealed class TestSocket : WebSocket
    {
        public readonly Channel<WebSocketReceiveResult> Incoming = Channel.CreateUnbounded<WebSocketReceiveResult>();
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
            var result = await Incoming.Reader.ReadAsync(token);
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
