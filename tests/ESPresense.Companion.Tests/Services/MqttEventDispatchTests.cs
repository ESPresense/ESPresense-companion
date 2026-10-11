using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Packets;
using Moq;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services;

/// <summary>
/// Regression tests for async multicast event dispatch.
///
/// C# multicast delegates only expose the last subscriber's <see cref="Task"/> when
/// invoked via <c>await SomeEvent(args)</c>, so earlier handlers could run to completion
/// unobserved (and their exceptions lost). <c>MqttCoordinator.InvokeAllAsync</c> awaits
/// every handler instead.
/// </summary>
public class MqttEventDispatchTests
{
    private static MqttCoordinator CreateCoordinator()
    {
        var configLoader = new Mock<ConfigLoader>("test-config-dir");
        return new MqttCoordinator(
            configLoader.Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<MqttCoordinator>>(),
            Mock.Of<IMqttNetLogger>(),
            new SupervisorConfigLoader(Mock.Of<Microsoft.Extensions.Logging.ILogger<SupervisorConfigLoader>>()));
    }

    private static MqttApplicationMessageReceivedEventArgs DeviceMessage(string deviceId, string nodeId, string payload)
    {
        var message = new MqttApplicationMessage
        {
            Topic = $"espresense/devices/{deviceId}/{nodeId}",
            PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload))
        };
        return new MqttApplicationMessageReceivedEventArgs("test-client", message, new MqttPublishPacket(), null!);
    }

    private static MqttApplicationMessageReceivedEventArgs Message(string topic, string? payload)
    {
        var message = new MqttApplicationMessage { Topic = topic };
        if (payload != null)
            message.PayloadSegment = new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload));
        return new MqttApplicationMessageReceivedEventArgs("test-client", message, new MqttPublishPacket(), null!);
    }

    [Test]
    public async Task OnMqttMessageReceived_InvokesAndAwaits_AllDeviceMessageHandlers()
    {
        var coordinator = CreateCoordinator();
        var completed = new List<string>();

        coordinator.DeviceMessageReceivedAsync += _ =>
        {
            completed.Add("first");
            return Task.CompletedTask;
        };
        coordinator.DeviceMessageReceivedAsync += async _ =>
        {
            // A handler that actually yields; only awaiting the last subscriber would
            // allow the message to be reported as handled before this finishes.
            await Task.Delay(50);
            completed.Add("second");
        };

        await coordinator.OnMqttMessageReceived(DeviceMessage("dev-1", "node-1", "{\"rssi\":-70}"));

        Assert.That(completed, Is.EqualTo(new[] { "first", "second" }),
            "both handlers must run and complete before dispatch returns");
    }

    [Test]
    public async Task OnMqttMessageReceived_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        // A non-last, slow handler. With `await SomeEvent(...)` this work would be
        // fire-and-forget and the flag could still be false when dispatch returns.
        coordinator.DeviceMessageReceivedAsync += async _ =>
        {
            await Task.Delay(75);
            slowFinished = true;
        };
        coordinator.DeviceMessageReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(DeviceMessage("dev-2", "node-2", "{\"rssi\":-60}"));

        Assert.That(slowFinished, Is.True, "the non-last handler's task must be awaited");
    }

    [Test]
    public async Task OnMqttMessageReceived_NonLastHandlerException_IsObservedNotUnobserved()
    {
        var coordinator = CreateCoordinator();
        var secondRan = false;

        // Throwing handler is first (non-last). Before the fix its faulted task was
        // never awaited, so the exception was unobserved. Now InvokeAllAsync awaits it,
        // and OnMqttMessageReceived's existing catch observes and logs it.
        coordinator.DeviceMessageReceivedAsync += _ => throw new InvalidOperationException("boom-first");
        coordinator.DeviceMessageReceivedAsync += _ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        };

        Assert.DoesNotThrowAsync(
            () => coordinator.OnMqttMessageReceived(DeviceMessage("dev-3", "node-3", "{\"rssi\":-50}")),
            "the dispatcher must observe and handle the handler exception without crashing");
        Assert.That(secondRan, Is.True, "the remaining handler must still run");
    }

    [Test]
    public async Task OnMqttMessageReceived_OneHandlerFailure_DoesNotStopOthers()
    {
        var coordinator = CreateCoordinator();
        var otherRan = false;

        coordinator.DeviceMessageReceivedAsync += _ => throw new InvalidOperationException("boom");
        coordinator.DeviceMessageReceivedAsync += _ =>
        {
            otherRan = true;
            return Task.CompletedTask;
        };

        try
        {
            await coordinator.OnMqttMessageReceived(DeviceMessage("dev-4", "node-4", "{\"rssi\":-40}"));
        }
        catch (Exception)
        {
            // Expected: the first handler's exception is rethrown after all handlers run.
        }

        Assert.That(otherRan, Is.True, "a failing handler must not prevent the rest from running");
    }

    [Test]
    public async Task InvokeAllAsync_SingleHandler_RunsOnce()
    {
        var coordinator = CreateCoordinator();
        var calls = 0;
        Func<DeviceMessageEventArgs, Task> handler = _ =>
        {
            calls++;
            return Task.CompletedTask;
        };

        await coordinator.InvokeAllAsync(handler, new DeviceMessageEventArgs { DeviceId = "d", NodeId = "n", Payload = new DeviceMessage() }, "TestEvent");

        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void InvokeAllAsync_SingleHandlerThrowsSynchronously_ReturnsFaultedTask()
    {
        var coordinator = CreateCoordinator();
        Func<DeviceMessageEventArgs, Task> handler = _ => throw new InvalidOperationException("boom");

        Task? task = null;
        Assert.DoesNotThrow(() => task = coordinator.InvokeAllAsync(handler, new DeviceMessageEventArgs(), "TestEvent"));

        Assert.ThrowsAsync<InvalidOperationException>(() => task!);
    }

    [Test]
    public async Task InvokeAllAsync_NullHandler_CompletesWithoutThrowing()
    {
        var coordinator = CreateCoordinator();

        Assert.DoesNotThrowAsync(() =>
            coordinator.InvokeAllAsync<DeviceMessageEventArgs>(null, new DeviceMessageEventArgs(), "TestEvent"));
    }

    [Test]
    public void InvokeAllAsync_MultipleHandlers_ThrowsAggregateException()
    {
        var coordinator = CreateCoordinator();
        Func<DeviceMessageEventArgs, Task> handlers = _ => throw new InvalidOperationException("a");
        handlers += _ => throw new InvalidOperationException("b");

        var ex = Assert.ThrowsAsync<AggregateException>(
            () => coordinator.InvokeAllAsync(handlers, new DeviceMessageEventArgs(), "TestEvent"));

        Assert.That(ex!.InnerExceptions, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task InvokeAllAsync_HandlerUnsubscribesDuringDispatch_StillCompletesAllInvokedHandlers()
    {
        var coordinator = CreateCoordinator();
        var firstRan = false;
        var secondRan = false;

        // Handlers are composed into a multicast delegate snapshot before dispatch.
        // Removing a handler from the live event inside another handler must not
        // change the snapshot that is already being dispatched.
        Func<DeviceMessageEventArgs, Task> second = _ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        };
        Func<DeviceMessageEventArgs, Task> first = _ =>
        {
            firstRan = true;
            coordinator.DeviceMessageReceivedAsync -= second; // mutate the live event
            return Task.CompletedTask;
        };

        coordinator.DeviceMessageReceivedAsync += first;
        coordinator.DeviceMessageReceivedAsync += second;

        Func<DeviceMessageEventArgs, Task> snapshot = first;
        snapshot += second;

        await coordinator.InvokeAllAsync(snapshot, new DeviceMessageEventArgs(), "TestEvent");

        Assert.That(firstRan, Is.True);
        Assert.That(secondRan, Is.True, "handlers present at dispatch time must all run");
    }

    [Test]
    public async Task InvokeAllAsync_ReentrantDispatch_DoesNotDeadlock()
    {
        var coordinator = CreateCoordinator();
        var depth = 0;

        Func<DeviceMessageEventArgs, Task> handler = null!;
        handler = async _ =>
        {
            depth++;
            if (depth == 1)
            {
                // Re-enter dispatch from within a handler.
                await coordinator.InvokeAllAsync(handler, new DeviceMessageEventArgs(), "TestEvent");
            }
        };

        var task = coordinator.InvokeAllAsync(handler, new DeviceMessageEventArgs(), "TestEvent");

        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.That(completed, Is.SameAs(task), "reentrant dispatch must not deadlock");
        await task;
    }

    [Test]
    public async Task OnMqttMessageReceived_TelemetryTopic_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.NodeTelemetryReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.NodeTelemetryReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(
            Message("espresense/rooms/node-1/telemetry", "{\"ip\":\"1.2.3.4\",\"uptime\":10}"));

        Assert.That(slowFinished, Is.True, "telemetry dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_StatusTopic_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.NodeStatusReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.NodeStatusReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(Message("espresense/rooms/node-1/status", "online"));

        Assert.That(slowFinished, Is.True, "status dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_StatusCleared_AwaitsNonLastRemovedHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.NodeStatusRemovedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.NodeStatusRemovedAsync += _ => Task.CompletedTask;

        // Null payload => retained message cleared => node removed.
        await coordinator.OnMqttMessageReceived(Message("espresense/rooms/node-1/status", null));

        Assert.That(slowFinished, Is.True, "status-removed dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_TelemetryCleared_AwaitsNonLastRemovedHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.NodeTelemetryRemovedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.NodeTelemetryRemovedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(Message("espresense/rooms/node-1/telemetry", null));

        Assert.That(slowFinished, Is.True, "telemetry-removed dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_SettingsConfigTopic_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.DeviceConfigReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.DeviceConfigReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(
            Message("espresense/settings/dev-1/config", "{\"name\":\"beacon\"}"));

        Assert.That(slowFinished, Is.True, "settings-config dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_NodeSettingTopic_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.NodeSettingReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.NodeSettingReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(Message("espresense/rooms/node-1/foo", "bar"));

        Assert.That(slowFinished, Is.True, "node-setting dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_AttributesTopic_AwaitsNonLastHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.DeviceAttributesReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.DeviceAttributesReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(
            Message("espresense/companion/dev-1/attributes", "{\"foo\":\"bar\"}"));

        Assert.That(slowFinished, Is.True, "attributes dispatch must await the non-last handler");
    }

    [Test]
    public async Task OnMqttMessageReceived_UnknownTopic_AwaitsNonLastMqttHandler()
    {
        var coordinator = CreateCoordinator();
        var slowFinished = false;

        coordinator.MqttMessageReceivedAsync += async _ =>
        {
            await Task.Delay(50);
            slowFinished = true;
        };
        coordinator.MqttMessageReceivedAsync += _ => Task.CompletedTask;

        await coordinator.OnMqttMessageReceived(Message("some/other/topic", "payload"));

        Assert.That(slowFinished, Is.True, "default-topic dispatch must await the non-last handler");
    }

    [Test]
    public async Task InvokeAllAsync_TrueConcurrentDispatches_AwaitEveryHandlerEachTime()
    {
        var coordinator = CreateCoordinator();
        const int dispatches = 200;
        const int handlersPerDispatch = 4;

        var perHandlerCounts = new int[handlersPerDispatch];
        Func<DeviceMessageEventArgs, Task> handlers = null!;
        for (var i = 0; i < handlersPerDispatch; i++)
        {
            var index = i;
            Func<DeviceMessageEventArgs, Task> h = async _ =>
            {
                await Task.Yield();
                Interlocked.Increment(ref perHandlerCounts[index]);
            };
            handlers = handlers == null ? h : handlers + h;
        }

        // Fire many independent dispatches on the thread pool at once.
        var tasks = Enumerable.Range(0, dispatches)
            .Select(_ => Task.Run(() => coordinator.InvokeAllAsync(handlers, new DeviceMessageEventArgs(), "TestEvent")))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.That(perHandlerCounts, Is.All.EqualTo(dispatches),
            "every handler must be awaited on every concurrent dispatch");
    }

    [Test]
    public async Task OnMqttMessageReceived_FailingHandler_IsDrainedAndReportedAsMalformed()
    {
        // OnMqttMessageReceived deliberately absorbs handler exceptions so one bad
        // message cannot kill the MQTT receive loop. The contract is: the exception is
        // awaited and pulled out of the handler (not left unobserved), the later handler
        // still runs, and MqttMessageMalformed is raised.
        var coordinator = CreateCoordinator();
        var malformed = 0;
        var lastRan = false;

        coordinator.MqttMessageMalformed += (_, _) => Interlocked.Increment(ref malformed);
        coordinator.DeviceMessageReceivedAsync += _ => throw new InvalidOperationException("boom");
        coordinator.DeviceMessageReceivedAsync += _ =>
        {
            lastRan = true;
            return Task.CompletedTask;
        };

        Assert.DoesNotThrowAsync(
            () => coordinator.OnMqttMessageReceived(DeviceMessage("dev-x", "node-x", "{\"rssi\":-60}")));

        await Task.Yield();
        Assert.That(lastRan, Is.True, "later handlers must still run after an earlier one fails");
        Assert.That(malformed, Is.EqualTo(1), "a handler failure must surface as MqttMessageMalformed");
    }
}
