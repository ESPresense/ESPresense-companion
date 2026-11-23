using System.Net.WebSockets;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ESPresense.Locators;
using MathNet.Spatial.Euclidean;
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
        var original = SubscriberCounts(fixture, events);
        var session = Create(fixture, events, CancellationToken.None);
        var run = session.RunAsync(socket, false, CancellationToken.None);
        Assert.That(await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)), Does.Contain("\"type\":\"time\""));
        Assert.That(SubscriberCounts(fixture, events), Is.EqualTo(original.Select(n => n + 1)));
        socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(0, WebSocketMessageType.Close, true), []));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(SubscriberCounts(fixture, events), Is.EqualTo(original));
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

    [Test]
    public async Task NonFiniteDeviceDoesNotInterruptOtherEventSubscribers()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);
        var laterSubscriberCalled = false;
        events.DeviceStateChanged += (_, _) => laterSubscriberCalled = true;
        var device = Tracked("non-finite");
        device.BestScenario = new Scenario(null, Mock.Of<ILocate>(), "test");
        device.UpdateLocation(new Point3D(double.NaN, 1, 2));
        Assert.That(double.IsNaN(device.Location!.Value.X), Is.True);

        Assert.DoesNotThrow(() => events.OnDeviceChanged(device, false));
        Assert.That(laterSubscriberCalled, Is.True);
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(socket.State, Is.EqualTo(WebSocketState.Aborted));
    }

    [Test]
    public async Task SubscriptionLimitIsIdempotentAndUnsubscribeReleasesSlot()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);
        for (var i = 0; i < StateWebSocketSession.MaxSubscriptions; i++)
            await Command(socket, Subscription("subscribe", $"dev-{i}"));
        await Command(socket, Subscription("subscribe", "dev-0"));
        await Command(socket, Subscription("unsubscribe", "dev-0"));
        await Command(socket, Subscription("subscribe", "replacement"));
        events.OnDeviceMessageReceived(Message("dev-0"));
        events.OnDeviceMessageReceived(Message("replacement"));
        Assert.That(await Next(socket), Does.Contain("\"deviceId\":\"replacement\""));
        await AssertNothingQueued(socket, events);
        var json = Subscription("subscribe", "over-limit");
        socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(Encoding.UTF8.GetByteCount(json), WebSocketMessageType.Text, true), Encoding.UTF8.GetBytes(json)));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
    }

    [Test]
    public async Task SubscriptionByteLimitReleasesBudgetOnUnsubscribe()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        var events = new GlobalEventDispatcher();
        var socket = new TestSocket();
        var run = Create(fixture, events, CancellationToken.None).RunAsync(socket, false, CancellationToken.None);
        await Next(socket);
        var id = new string('x', StateWebSocketSession.MaxSubscriptionBytes / 2);
        await Command(socket, Subscription("subscribe", id));
        await Command(socket, Subscription("unsubscribe", id));
        await Command(socket, Subscription("subscribe", id + "1"));
        await Command(socket, Subscription("subscribe", "small"));
        events.OnDeviceMessageReceived(Message("small"));
        Assert.That(await Next(socket), Does.Contain("\"deviceId\":\"small\""));
        // The first unsubscribe must have released the byte budget; this new distinct
        // ID now takes the total over the cap even though the count remains small.
        QueueCommand(socket, Subscription("subscribe", id + "2"));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
    }

    private static string Subscription(string command, string id) =>
        $"{{\"command\":\"{command}\",\"type\":\"deviceMessage\",\"value\":\"{id}\"}}";

    private static int[] SubscriberCounts(CalibrationFixture fixture, GlobalEventDispatcher events) =>
        new[] { "CalibrationChanged", "NodeStateChanged", "DeviceStateChanged", "DeviceMessageReceived", "DeviceRemoved", "LocatorStateChanged" }
            .Select(name => Count(events, name)).Append(Count(fixture.ConfigLoader, "ConfigChanged")).ToArray();

    private static int Count(object publisher, string name) =>
        ((Delegate?)publisher.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(publisher))?.GetInvocationList().Length ?? 0;

    [Test]
    public async Task ManagedSocketPeerCloseDuringSendPreservesCloseFrame()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        using var transport = await SocketTransport.CreateAsync();
        var run = Create(fixture, new GlobalEventDispatcher(), CancellationToken.None)
            .RunAsync(transport.Server, false, CancellationToken.None);
        await transport.Stream.Sending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await transport.Peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await WaitUntil(() => transport.Server.State == WebSocketState.CloseReceived);
        Assert.That(run.IsCompleted, Is.False, "the in-flight send is allowed to finish");
        transport.Stream.Release.TrySetResult();
        var result = await ReceiveClose(transport.Peer);
        Assert.That(result.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(transport.Stream.PendingWrites, Is.Zero);
    }

    [TestCase(WebSocketMessageType.Binary, 1, WebSocketCloseStatus.InvalidMessageType)]
    [TestCase(WebSocketMessageType.Text, StateWebSocketSession.MaxCommandBytes + 1, WebSocketCloseStatus.MessageTooBig)]
    public async Task ManagedSocketRejectedInputSendsChosenStatus(WebSocketMessageType type, int size, WebSocketCloseStatus status)
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        using var transport = await SocketTransport.CreateAsync();
        transport.Stream.Release.TrySetResult();
        var run = Create(fixture, new GlobalEventDispatcher(), CancellationToken.None)
            .RunAsync(transport.Server, false, CancellationToken.None);
        await transport.Peer.SendAsync(new ArraySegment<byte>(new byte[size]), type, true, CancellationToken.None);
        Assert.That((await ReceiveClose(transport.Peer)).CloseStatus, Is.EqualTo(status));
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ManagedSocketDeadlineAbortsAndJoinsBlockedSend()
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        using var transport = await SocketTransport.CreateAsync();
        // Deliberately leave the write pending even if its token is cancelled. Abort must
        // dispose the underlying transport, release the write, and join it before returning.
        transport.Stream.IgnoreWriteCancellation = true;
        transport.Stream.HoldWriteCleanup = true;
        var run = Create(fixture, new GlobalEventDispatcher(), CancellationToken.None)
            .RunAsync(transport.Server, false, CancellationToken.None);
        await transport.Stream.Sending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await transport.Peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await transport.Stream.WriteFinishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Abort has released transport I/O, but RunAsync must also wait for its asynchronous
        // unwind. Keep that unwind gated so a fire-and-forget continuation cannot pass.
        await Task.WhenAny(run, Task.Delay(100));
        var returnedBeforeJoin = run.IsCompleted;
        transport.Stream.ReleaseCleanup.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(returnedBeforeJoin, Is.False);
        Assert.That(transport.Server.State, Is.EqualTo(WebSocketState.Aborted));
        Assert.That(transport.Stream.PendingWrites, Is.Zero, "RunAsync joins the aborted send");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ManagedSocketCancellationJoinsPendingReadAndWrite(bool hostStop)
    {
        await using var fixture = await CalibrationFixture.CreateAsync();
        using var transport = await SocketTransport.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var run = Create(fixture, new GlobalEventDispatcher(), hostStop ? cancel.Token : CancellationToken.None)
            .RunAsync(transport.Server, false, hostStop ? CancellationToken.None : cancel.Token);
        await transport.Stream.Sending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(transport.Server.State, Is.EqualTo(WebSocketState.Aborted));
        Assert.That(transport.Stream.PendingWrites, Is.Zero);
        Assert.That(transport.Stream.PendingReads, Is.Zero);
    }

    private static async Task<WebSocketReceiveResult> ReceiveClose(WebSocket peer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        WebSocketReceiveResult result;
        do { result = await peer.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token); }
        while (result.MessageType != WebSocketMessageType.Close);
        return result;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    /// <summary>Real .NET ManagedWebSockets over loopback TCP; the wrapper controls write timing only.</summary>
    private sealed class SocketTransport(TcpClient client, TcpClient accepted, GatedStream stream) : IDisposable
    {
        public GatedStream Stream { get; } = stream;
        public WebSocket Server { get; } = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        public WebSocket Peer { get; } = WebSocket.CreateFromStream(client.GetStream(), false, null, Timeout.InfiniteTimeSpan);

        public static async Task<SocketTransport> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var accepted = await listener.AcceptTcpClientAsync();
            return new SocketTransport(client, accepted, new GatedStream(accepted.GetStream()));
        }

        public void Dispose()
        {
            Server.Dispose();
            Peer.Dispose();
            accepted.Dispose();
            client.Dispose();
        }
    }

    private sealed class GatedStream(Stream inner) : Stream
    {
        public TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreWriteCancellation;
        public bool HoldWriteCleanup;
        public TaskCompletionSource WriteFinishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PendingWrites;
        public int PendingReads;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PendingReads);
            try { return await inner.ReadAsync(buffer, cancellationToken); }
            finally { Interlocked.Decrement(ref PendingReads); }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PendingWrites);
            Sending.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(IgnoreWriteCancellation ? CancellationToken.None : cancellationToken);
                await inner.WriteAsync(buffer, cancellationToken);
            }
            finally
            {
                WriteFinishing.TrySetResult();
                if (HoldWriteCleanup) await ReleaseCleanup.Task;
                Interlocked.Decrement(ref PendingWrites);
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release.TrySetException(new ObjectDisposedException(nameof(GatedStream)));
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
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
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            var chunk = bytes.Skip(offset).Take(4096).ToArray();
            socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(chunk.Length, WebSocketMessageType.Text, offset + chunk.Length == bytes.Length), chunk));
            await socket.Reads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void QueueCommand(TestSocket socket, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            var chunk = bytes.Skip(offset).Take(4096).ToArray();
            socket.Incoming.Writer.TryWrite((new WebSocketReceiveResult(chunk.Length, WebSocketMessageType.Text, offset + chunk.Length == bytes.Length), chunk));
        }
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
        private WebSocketCloseStatus? _closeStatus;
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => CloseOutputAsync(status, description, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) { _closeStatus = status; _state = WebSocketState.Closed; return Task.CompletedTask; }
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
