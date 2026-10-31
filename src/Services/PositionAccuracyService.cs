using ESPresense.Models;
using ESPresense.Utils;
using Newtonsoft.Json;
using Serilog;

namespace ESPresense.Services;

/// <summary>
/// Runs the node self-test on a schedule and publishes it as a Home Assistant sensor.
/// </summary>
public class PositionAccuracyService(State state, IMqttCoordinator mqtt) : BackgroundService
{
    // Not espresense/companion/+/attributes: that's device attributes, and would create a device called accuracy
    internal const string StateTopic = "espresense/companion/accuracy/state";
    internal const string DetailsTopic = "espresense/companion/accuracy/details";

    private static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private PositionAccuracy? _latest;

    /// <summary>
    /// The last result, or a fresh one if the schedule hasn't run yet
    /// </summary>
    public PositionAccuracy Latest => _latest ??= PositionAccuracy.Measure(state);

    internal async Task RunOnce()
    {
        var accuracy = _latest = PositionAccuracy.Measure(state);

        // Sent every run so the sensor comes back if the retained config is lost
        await Discovery().Send(mqtt);
        if (accuracy.Median is { } median)
            await mqtt.EnqueueAsync(StateTopic, $"{median:F2}", true);
        await mqtt.EnqueueAsync(DetailsTopic, JsonConvert.SerializeObject(new
        {
            median_m = accuracy.Median,
            p95_m = accuracy.P95,
            worst_node = accuracy.Worst?.Name ?? accuracy.Worst?.Id,
            worst_link_error_m = accuracy.Worst?.LinkError,
            placed = accuracy.Placed,
            solved = accuracy.Solved,
            unsolved = accuracy.Unsolved,
            guidance = accuracy.Guidance,
            last_tested = accuracy.Timestamp
        }, SerializerSettings.NullIgnore), true);
    }

    private static AutoDiscovery Discovery() => new("sensor", "espresense_companion_position_accuracy", new AutoDiscovery.DiscoveryRecord
    {
        Name = "Position Accuracy",
        UniqueId = "espresense-companion-position-accuracy",
        StateTopic = StateTopic,
        JsonAttributesTopic = DetailsTopic,
        EntityStatusTopic = "espresense/companion/status",
        UnitOfMeasurement = "m",
        DeviceClass = "distance",
        StateClass = "measurement",
        Device = new AutoDiscovery.DeviceRecord
        {
            Name = "ESPresense Companion",
            Manufacturer = "ESPresense",
            Model = "Companion",
            Identifiers = ["espresense-companion"]
        },
        Origin = new AutoDiscovery.OriginRecord { Name = "ESPresense Companion" }
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(SettleTime, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Position accuracy self-test failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
