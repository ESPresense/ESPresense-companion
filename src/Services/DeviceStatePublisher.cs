using System.Runtime.CompilerServices;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Services;

/// <summary>What was last published for one device.</summary>
/// <param name="State">The last state topic value the broker accepted.</param>
/// <param name="Location">Where the last accepted attributes put the device.</param>
/// <param name="BestScenario">The best_scenario of the last accepted attributes.</param>
/// <param name="PreviousState">The accepted state before <paramref name="State"/>, for explanations.</param>
/// <param name="StateChanged">When <paramref name="State"/> was accepted.</param>
/// <param name="AttributesPending">The last attributes publish failed, so the next report retries it.</param>
public sealed record ReportedDeviceState(
    string? State = null,
    Point3D? Location = null,
    string? BestScenario = null,
    string? PreviousState = null,
    DateTime? StateChanged = null,
    bool AttributesPending = false);

/// <param name="StateChanged">The report's state differed from the last accepted one (a publish was attempted).</param>
/// <param name="AttributesPublished">The attributes went out.</param>
public readonly record struct PublishOutcome(bool StateChanged, bool AttributesPublished);

public interface IDeviceStatePublisher
{
    /// <summary>
    /// Publishes <paramref name="report"/> to espresense/companion/{id} (state) and
    /// espresense/companion/{id}/attributes, both retained.
    /// <list type="bullet">
    /// <item>The state is sent when it differs from the last state the broker accepted.</item>
    /// <item>The attributes are sent when the report has a location and
    /// <paramref name="locationChanged"/>, the state changed, or the last attributes publish failed.</item>
    /// </list>
    /// </summary>
    /// <param name="locationChanged">The caller's view that the position moved since it was last reported.</param>
    Task<PublishOutcome> PublishAsync(Device device, DeviceReport report, bool locationChanged, CancellationToken ct = default);

    /// <summary>What was last published for <paramref name="device"/>; empty if nothing yet.</summary>
    ReportedDeviceState Reported(Device device);
}

/// <summary>
/// Owns the "last reported" bookkeeping for each device so <see cref="Device"/> doesn't carry it.
/// Keyed by the <see cref="Device"/> instance, not its id: a device deleted and seen again is a new
/// instance whose retained topics were cleared, so it must publish from scratch.
/// </summary>
public class DeviceStatePublisher(IMqttCoordinator mqtt) : IDeviceStatePublisher
{
    private static readonly ReportedDeviceState Nothing = new();
    private readonly ConditionalWeakTable<Device, ReportedDeviceState> _reported = new();

    public ReportedDeviceState Reported(Device device) => _reported.TryGetValue(device, out var r) ? r : Nothing;

    public async Task<PublishOutcome> PublishAsync(Device device, DeviceReport report, bool locationChanged, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var last = Reported(device);

        var stateChanged = report.State != last.State;
        if (stateChanged && await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}", report.State, retain: true))
            last = Remember(device, last with { State = report.State, PreviousState = last.State, StateChanged = DateTime.UtcNow });

        var attributes = report.AttributesJson();
        if (attributes == null || !(locationChanged || stateChanged || last.AttributesPending))
            return new PublishOutcome(stateChanged, false);

        var sent = await mqtt.TryEnqueueAsync($"espresense/companion/{device.Id}/attributes", attributes, retain: true);
        Remember(device, sent
            ? last with { Location = report.Location, BestScenario = report.BestScenario, AttributesPending = false }
            : last with { AttributesPending = true });
        return new PublishOutcome(stateChanged, sent);
    }

    /// <summary>Records <paramref name="state"/> as published; tests use it to seed a prior report.</summary>
    internal ReportedDeviceState Remember(Device device, ReportedDeviceState state)
    {
        _reported.AddOrUpdate(device, state);
        return state;
    }
}
