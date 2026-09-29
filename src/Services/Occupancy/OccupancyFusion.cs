namespace ESPresense.Services.Occupancy;

/// <summary>Which signal produced a room's occupancy read.</summary>
public enum OccupancySource
{
    None,
    Ble,
    Csi
}

/// <summary>A fused per-room occupancy read (CMP-1, ESPA-197).</summary>
public sealed record RoomOccupancy(string RoomId, bool IsOccupied, double Confidence, OccupancySource Source);

/// <summary>
/// Fuses existing BLE device-based room presence with the new CSI device-free signal into one
/// room-occupancy read. Per the ESPA-168 feasibility doc's augmentation thesis, CSI augments BLE
/// for people not carrying a tracked device — it does not replace or outvote a tracked device BLE
/// already places in the room. This is the first-target consumer for CMP-2's HVAC automation.
/// </summary>
public sealed class OccupancyFusion
{
    private readonly double _csiConfidenceThreshold;

    public OccupancyFusion(double csiConfidenceThreshold = 0.6)
    {
        _csiConfidenceThreshold = csiConfidenceThreshold;
    }

    public RoomOccupancy Fuse(string roomId, bool bleDevicePresent, CsiOccupancySignal? csi)
    {
        if (bleDevicePresent)
            return new RoomOccupancy(roomId, true, 1.0, OccupancySource.Ble);

        if (csi is { } signal)
            return new RoomOccupancy(roomId, signal.Confidence >= _csiConfidenceThreshold, signal.Confidence, OccupancySource.Csi);

        return new RoomOccupancy(roomId, false, 0.0, OccupancySource.None);
    }
}
