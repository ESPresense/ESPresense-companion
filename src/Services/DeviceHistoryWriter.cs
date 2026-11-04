using ESPresense.Models;

namespace ESPresense.Services;

public interface IDeviceHistoryWriter
{
    /// <summary>Stores one row per scenario with a non-zero confidence, when history is enabled.</summary>
    Task RecordAsync(Device device, Scenario? best, CancellationToken ct = default);
}

public class DeviceHistoryWriter(DeviceHistoryStore store, State state) : IDeviceHistoryWriter
{
    public async Task RecordAsync(Device device, Scenario? best, CancellationToken ct = default)
    {
        if (!(state.Config?.History?.Enabled ?? false)) return;

        foreach (var ds in device.Scenarios.Where(ds => ds.Confidence != 0))
        {
            ct.ThrowIfCancellationRequested();
            await store.Add(new DeviceHistory
            {
                Id = device.Id,
                When = DateTime.UtcNow,
                X = ds.Location.X,
                Y = ds.Location.Y,
                Z = ds.Location.Z,
                Confidence = ds.Confidence ?? 0,
                Fixes = ds.Fixes ?? 0,
                Scenario = ds.Name,
                Best = ds == best
            });
        }
    }
}
