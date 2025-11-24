using ESPresense.Models;
using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class TelemetryService(MqttCoordinator mqtt) : BackgroundService
{
    public Telemetry Telemetry { get; } = new() { Ip = IpUtils.GetLocalIpAddress() };

    public void IncrementMalformedMessages()
    {
        Telemetry.Malformed++;
    }

    public void IncrementMessages()
    {
        Telemetry.Messages++;
    }

    public void IncrementMoved()
    {
        Telemetry.Moved++;
    }

    public void IncrementSkipped()
    {
        Telemetry.Skipped++;
    }

    public void UpdateTrackedDevices(int count)
    {
        Telemetry.Tracked = count;
    }

    public bool AddUnknownNode(string nodeId)
    {
        return Telemetry.UnknownNodes.Add(nodeId);
    }

    public void UpdateDevicesCount(int count)
    {
        Telemetry.Devices = count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await mqtt.EnqueueAsync("espresense/companion/telemetry", JsonConvert.SerializeObject(Telemetry, SerializerSettings.NullIgnore));
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}