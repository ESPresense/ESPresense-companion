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

        await coordinator.InvokeAllAsync(handler, new DeviceMessageEventArgs { DeviceId = "d", NodeId = "n", Payload = new DeviceMessage() });

        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task InvokeAllAsync_NullHandler_CompletesWithoutThrowing()
    {
        var coordinator = CreateCoordinator();

        Assert.DoesNotThrowAsync(() =>
            coordinator.InvokeAllAsync<DeviceMessageEventArgs>(null, new DeviceMessageEventArgs()));
    }

    [Test]
    public void InvokeAllAsync_MultipleHandlers_ThrowsAggregateException()
    {
        var coordinator = CreateCoordinator();
        Func<DeviceMessageEventArgs, Task> handlers = _ => throw new InvalidOperationException("a");
        handlers += _ => throw new InvalidOperationException("b");

        var ex = Assert.ThrowsAsync<AggregateException>(
            () => coordinator.InvokeAllAsync(handlers, new DeviceMessageEventArgs()));

        Assert.That(ex!.InnerExceptions, Has.Count.EqualTo(2));
    }
}
