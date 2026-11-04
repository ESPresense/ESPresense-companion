using System.Text;
using MQTTnet;
using MQTTnet.Diagnostics.PacketInspection;
using MQTTnet.Packets;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// In-memory <see cref="IMqttClient"/> for connection tests. Mimics the parts of MQTTnet 5 the companion relies
/// on: ConnectAsync awaits the ConnectedAsync handlers inside the connect (a throwing handler fails the connect),
/// and a drop raises DisconnectedAsync.
/// </summary>
internal sealed class FakeMqttClient : IMqttClient
{
    private readonly object _sync = new();

    public record Publish(string Topic, string? Payload, bool Retain);

    public List<Publish> Published { get; } = new();
    public List<string> Subscribed { get; } = new();
    public List<string> Unsubscribed { get; } = new();
    public int ConnectCalls;
    public int DisconnectCalls;
    public bool Disposed { get; private set; }

    /// <summary>Number of upcoming ConnectAsync calls that fail with a communication error.</summary>
    public int ConnectFailures;

    /// <summary>Awaited at the start of every ConnectAsync, to hold a connect in flight.</summary>
    public Func<Task>? ConnectGate;

    /// <summary>Called after each subscribe, e.g. to deliver retained messages.</summary>
    public Func<string, Task>? OnSubscribed;

    public bool IsConnected { get; private set; }
    public MqttClientOptions Options { get; private set; } = null!;

    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? ApplicationMessageReceivedAsync;
    public event Func<MqttClientConnectedEventArgs, Task>? ConnectedAsync;
    public event Func<MqttClientConnectingEventArgs, Task>? ConnectingAsync;
    public event Func<MqttClientDisconnectedEventArgs, Task>? DisconnectedAsync;
    public event Func<InspectMqttPacketEventArgs, Task>? InspectPacketAsync { add { } remove { } }

    public async Task<MqttClientConnectResult> ConnectAsync(MqttClientOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Interlocked.Increment(ref ConnectCalls);
        if (ConnectGate != null) await ConnectGate();

        Options = options;
        if (ConnectingAsync != null) await ConnectingAsync(new MqttClientConnectingEventArgs(options));

        if (ConnectFailures > 0)
        {
            ConnectFailures--;
            throw new MQTTnet.Exceptions.MqttCommunicationException("fake connect failure");
        }

        var result = new MqttClientConnectResult();
        IsConnected = true;
        try
        {
            if (ConnectedAsync != null) await ConnectedAsync(new MqttClientConnectedEventArgs(result));
        }
        catch
        {
            await RaiseDisconnected(clientWasConnected: true, MqttClientDisconnectReason.UnspecifiedError);
            throw;
        }

        return result;
    }

    public async Task DisconnectAsync(MqttClientDisconnectOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref DisconnectCalls);
        if (IsConnected) await RaiseDisconnected(clientWasConnected: true, MqttClientDisconnectReason.NormalDisconnection);
    }

    /// <summary>Simulates the broker dropping the connection.</summary>
    public Task DropAsync() => RaiseDisconnected(clientWasConnected: true, MqttClientDisconnectReason.UnspecifiedError);

    /// <summary>Marks the client disconnected without raising DisconnectedAsync (a drop not yet noticed).</summary>
    public void LoseConnectionSilently() => IsConnected = false;

    private Task RaiseDisconnected(bool clientWasConnected, MqttClientDisconnectReason reason)
    {
        IsConnected = false;
        var handlers = DisconnectedAsync;
        return handlers == null
            ? Task.CompletedTask
            : handlers(new MqttClientDisconnectedEventArgs(clientWasConnected, null!, reason, "", new List<MqttUserProperty>(), null!));
    }

    public Task<MqttClientPublishResult> PublishAsync(MqttApplicationMessage applicationMessage, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!IsConnected) throw new MQTTnet.Exceptions.MqttClientNotConnectedException();
        lock (_sync)
            Published.Add(new Publish(applicationMessage.Topic, applicationMessage.Payload.IsEmpty ? null : applicationMessage.ConvertPayloadToString(), applicationMessage.Retain));
        return Task.FromResult(new MqttClientPublishResult(null, MqttClientPublishReasonCode.Success, "", new List<MqttUserProperty>()));
    }

    public async Task<MqttClientSubscribeResult> SubscribeAsync(MqttClientSubscribeOptions options, CancellationToken cancellationToken = default)
    {
        foreach (var filter in options.TopicFilters)
        {
            lock (_sync) Subscribed.Add(filter.Topic);
            if (OnSubscribed != null) await OnSubscribed(filter.Topic);
        }
        return new MqttClientSubscribeResult(0, new List<MqttClientSubscribeResultItem>(), "", new List<MqttUserProperty>());
    }

    public Task<MqttClientUnsubscribeResult> UnsubscribeAsync(MqttClientUnsubscribeOptions options, CancellationToken cancellationToken = default)
    {
        lock (_sync) Unsubscribed.AddRange(options.TopicFilters);
        return Task.FromResult(new MqttClientUnsubscribeResult(0, new List<MqttClientUnsubscribeResultItem>(), "", new List<MqttUserProperty>()));
    }

    /// <summary>Delivers an inbound message to the receive handlers.</summary>
    public Task DeliverAsync(string topic, string? payload, bool retain = false)
    {
        var message = new MqttApplicationMessage { Topic = topic, Retain = retain };
        if (payload != null) message.PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload));
        var handlers = ApplicationMessageReceivedAsync;
        if (handlers == null) return Task.CompletedTask;
        return Task.WhenAll(handlers.GetInvocationList()
            .Cast<Func<MqttApplicationMessageReceivedEventArgs, Task>>()
            .Select(h => h(new MqttApplicationMessageReceivedEventArgs("fake", message, new MqttPublishPacket(), null!))));
    }

    public Task PingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendEnhancedAuthenticationExchangeDataAsync(MqttEnhancedAuthenticationExchangeData data, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose() => Disposed = true;
}
