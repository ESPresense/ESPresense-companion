using ESPresense.Events;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>Minimal scaffolding to drive the real <see cref="CsiFusionService"/>/<see cref="CsiMotionTracker"/>
/// outside ASP.NET Core, shared by <see cref="SyntheticFixtureBuilder"/> and the real-HA-history replay
/// (<c>fusion-replay-real</c>).</summary>
public sealed class MockConfigLoaderForFusion : ConfigLoader
{
    private readonly Config _config;
    public MockConfigLoaderForFusion(Config config) : base(string.Empty)
    {
        _config = config;
        typeof(ConfigLoader).GetProperty(nameof(Config))!.SetValue(this, config);
    }
    public new Config Config => _config;
}

public sealed class MockNodeTelemetryStoreForFusion : NodeTelemetryStore
{
    public MockNodeTelemetryStoreForFusion() : base(null!) { }
    public override bool Online(string id) => true;
    public override NodeTelemetry? Get(string id) => null;
}

/// <summary>Minimal no-op IMqttCoordinator -- this console project has no Moq dependency, and
/// CsiMotionTracker/CsiFusionService only need something to subscribe their events to.</summary>
public sealed class NullMqttCoordinator : IMqttCoordinator
{
    public string DiscoveryTopic => "homeassistant";
    public event Func<DeviceSettingsEventArgs, Task>? DeviceConfigReceivedAsync;
    public event Func<DeviceMessageEventArgs, Task>? DeviceMessageReceivedAsync;
    public event Func<MqttApplicationMessageReceivedEventArgs, Task>? MqttMessageReceivedAsync;
    public event Func<NodeSettingReceivedEventArgs, Task>? NodeSettingReceivedAsync;
    public event Func<NodeCsiVarianceReceivedEventArgs, Task>? NodeCsiVarianceReceivedAsync;
    public event Func<NodeTelemetryReceivedEventArgs, Task>? NodeTelemetryReceivedAsync;
    public event Func<NodeTelemetryRemovedEventArgs, Task>? NodeTelemetryRemovedAsync;
    public event Func<NodeStatusReceivedEventArgs, Task>? NodeStatusReceivedAsync;
    public event Func<NodeStatusRemovedEventArgs, Task>? NodeStatusRemovedAsync;
    public event Func<NodeUpdateErrorEventArgs, Task>? NodeUpdateErrorAsync;
    public event EventHandler? MqttMessageMalformed;
    public event EventHandler<PreviousDeviceDiscoveredEventArgs>? PreviousDeviceDiscovered;
    public event Func<DeviceAttributesEventArgs, Task>? DeviceAttributesReceivedAsync;

    public Task EnqueueAsync(string topic, string? payload, bool retain = false) => Task.CompletedTask;
    public Task<bool> TryEnqueueAsync(string topic, string? payload, bool retain = false) => Task.FromResult(true);
    public Task WaitForConnectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ClearRetainedAsync(string topicFilter, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
