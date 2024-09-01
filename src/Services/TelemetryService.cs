using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Services;

public class TelemetryService(MqttCoordinator mqtt) : BackgroundService
{
    private readonly Locators.Telemetry _telemetry = new() { Ip = IpUtils.GetLocalIpAddress() };

    public void IncrementMalformedMessages()
    {
        _telemetry.Malformed++;
    }

    public void IncrementMessages()
    {
        _telemetry.Messages++;
    }

    public void IncrementMoved()
    {
        _telemetry.Moved++;
    }

    public void IncrementSkipped()
    {
        _telemetry.Skipped++;
    }

    public void UpdateTrackedDevices(int count)
    {
        _telemetry.Tracked = count;
    }

    public bool AddUnknownNode(string nodeId)
    {
        return _telemetry.UnknownNodes.Add(nodeId);
    }

    public void UpdateDevicesCount(int count)
    {
        _telemetry.Devices = count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await mqtt.EnqueueAsync("espresense/companion/telemetry", JsonConvert.SerializeObject(_telemetry, SerializerSettings.NullIgnore));
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}