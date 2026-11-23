using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ESPresense.Controllers;
using ESPresense.Events;
using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Services;

/// <summary>Owns a single state stream. Overflow disconnects the client so it can reconnect and refresh.</summary>
public sealed class StateWebSocketSession(
    ConfigLoader config, GlobalEventDispatcher events, DeviceEnricher enricher,
    IHostApplicationLifetime lifetime, ILogger<StateWebSocketSession> logger)
{
    public const int OutboundCapacity = 256;
    public const int MaxCommandBytes = 64 * 1024;
    // Retain at most 128 device IDs and 64 KiB of UTF-8 ID data per connection.
    // A new distinct subscription beyond either cap closes with PolicyViolation.
    // Unsubscribe releases both budgets; duplicate subscriptions are idempotent.
    public const int MaxSubscriptions = 128;
    public const int MaxSubscriptionBytes = 64 * 1024;
    public const int MaxEventBytes = 1024 * 1024;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    public async Task RunAsync(WebSocket socket, bool showAll, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        using var abortOnStop = stop.Token.Register(socket.Abort);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait
        });
        var subscriptions = new ConcurrentDictionary<string, byte>();
        var subscriptionBytes = 0; // Only the receive loop mutates subscriptions and its budget.
        var enqueueLock = new object();
        var active = true;
        string? include = null;
        int all = showAll ? 1 : 0;
        var closeStatus = WebSocketCloseStatus.NormalClosure;

        void Enqueue<T>(T value)
        {
            lock (enqueueLock)
            {
                if (!active || stop.IsCancellationRequested) return;
                try
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.WebSocket);
                    if (bytes.Length > MaxEventBytes || !channel.Writer.TryWrite(bytes)) stop.Cancel();
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
                {
                    // A bad payload must not escape into the shared event publisher. Disconnect
                    // rather than silently lose state; reconnect refreshes the client's snapshot.
                    logger.LogWarning(ex, "Unable to serialize state WebSocket event");
                    stop.Cancel();
                }
            }
        }
        void OnConfig(object? s, Config e) => Enqueue(new { type = "configChanged" });
        void OnCalibration(object? s, CalibrationEventArgs e) => Enqueue(new { type = "calibrationChanged", data = e.Calibration });
        void OnNode(object? s, NodeStateEventArgs e) => Enqueue(new { type = "nodeStateChanged", data = e.NodeState });
        void OnRemoved(object? s, DeviceRemovedEventArgs e) => Enqueue(new { type = "deviceRemoved", deviceId = e.DeviceId });
        void OnLocator(object? s, LocatorStateEventArgs e) => Enqueue(new { type = "locatorStateChanged", data = e.LocatorState });
        void OnDevice(object? s, DeviceEventArgs e)
        {
            if (Volatile.Read(ref all) != 0 || (e.Device?.Track ?? false) || e.TrackChanged ||
                (!string.IsNullOrEmpty(Volatile.Read(ref include)) && string.Equals(e.Device?.Id, Volatile.Read(ref include), StringComparison.OrdinalIgnoreCase)))
            {
                if (e.Device != null) enricher.Enrich(e.Device);
                Enqueue(new { type = "deviceChanged", data = e.Device });
            }
        }
        void OnMessage(object? s, DeviceMessageEventArgs e)
        {
            if (subscriptions.ContainsKey(e.DeviceId))
                Enqueue(new { type = "deviceMessage", deviceId = e.DeviceId, nodeId = e.NodeId, data = e.Payload });
        }

        async Task SendAsync()
        {
            await foreach (var bytes in channel.Reader.ReadAllAsync(stop.Token))
            {
                // Receive-driven shutdown lets an in-flight send finish, but discards the backlog.
                if (!Volatile.Read(ref active)) return;
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, stop.Token);
            }
        }
        async Task ReceiveAsync()
        {
            var buffer = new byte[4096];
            while (!stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        closeStatus = WebSocketCloseStatus.InvalidMessageType;
                        return;
                    }
                    if (message.Length + result.Count > MaxCommandBytes)
                    {
                        closeStatus = WebSocketCloseStatus.MessageTooBig;
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                try
                {
                    var command = JsonSerializer.Deserialize<WebSocketCommand>(message.ToArray(), JsonDefaults.WebSocket);
                    switch (command?.Command?.ToLowerInvariant())
                    {
                        case "changefilter":
                            if (command.Type == "showAll") Volatile.Write(ref all, command.Value == "true" ? 1 : 0);
                            else if (command.Type == "include") Volatile.Write(ref include, command.Value);
                            break;
                        case "subscribe":
                            if (command.Type == "deviceMessage" && !string.IsNullOrEmpty(command.Value) && !subscriptions.ContainsKey(command.Value))
                            {
                                var bytes = Encoding.UTF8.GetByteCount(command.Value);
                                if (subscriptions.Count >= MaxSubscriptions || subscriptionBytes + bytes > MaxSubscriptionBytes)
                                {
                                    closeStatus = WebSocketCloseStatus.PolicyViolation;
                                    return;
                                }
                                subscriptions[command.Value] = 0;
                                subscriptionBytes += bytes;
                            }
                            break;
                        case "unsubscribe":
                            if (command.Type == "deviceMessage" && !string.IsNullOrEmpty(command.Value) && subscriptions.TryRemove(command.Value, out _))
                                subscriptionBytes -= Encoding.UTF8.GetByteCount(command.Value);
                            break;
                    }
                }
                catch (JsonException ex) { logger.LogDebug(ex, "Invalid WebSocket command"); }
            }
        }

        async Task ObserveLoopsAsync(Task both)
        {
            try { await both; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (WebSocketException ex) { logger.LogDebug(ex, "WebSocket disconnected after abort"); }
        }

        config.ConfigChanged += OnConfig;
        events.CalibrationChanged += OnCalibration;
        events.NodeStateChanged += OnNode;
        events.DeviceStateChanged += OnDevice;
        events.DeviceMessageReceived += OnMessage;
        events.DeviceRemoved += OnRemoved;
        events.LocatorStateChanged += OnLocator;
        try
        {
            Enqueue(new { type = "time", data = DateTime.UtcNow.RelativeMilliseconds() });
            var send = SendAsync();
            var receive = ReceiveAsync();
            await Task.WhenAny(send, receive, Task.Delay(Timeout.InfiniteTimeSpan, stop.Token));
            lock (enqueueLock) active = false;
            channel.Writer.TryComplete();
            // Only a successful receive exit gets a graceful close. Cancellation and loop
            // faults abort immediately, waking both supported ManagedWebSocket I/O loops.
            var graceful = receive.IsCompletedSuccessfully && !stop.IsCancellationRequested && !send.IsFaulted;
            if (!graceful) stop.Cancel();
            var both = Task.WhenAll(send, receive);
            try
            {
                await both.WaitAsync(ShutdownTimeout);
            }
            catch (TimeoutException)
            {
                stop.Cancel(); // Abort at the deadline, then join before the controller disposes.
                await ObserveLoopsAsync(both);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (WebSocketException ex) { logger.LogDebug(ex, "WebSocket disconnected"); }

            if (!stop.IsCancellationRequested && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(ShutdownTimeout);
                using var abortOnCloseTimeout = closeTimeout.Token.Register(socket.Abort);
                try { await socket.CloseOutputAsync(closeStatus, null, closeTimeout.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { socket.Abort(); }
            }
        }
        finally
        {
            lock (enqueueLock) active = false;
            abortOnStop.Dispose();
            stop.Cancel(); // Release the cancellation waiter without aborting a graceful close.
            channel.Writer.TryComplete();
            config.ConfigChanged -= OnConfig;
            events.CalibrationChanged -= OnCalibration;
            events.NodeStateChanged -= OnNode;
            events.DeviceStateChanged -= OnDevice;
            events.DeviceMessageReceived -= OnMessage;
            events.DeviceRemoved -= OnRemoved;
            events.LocatorStateChanged -= OnLocator;
        }
    }
}
