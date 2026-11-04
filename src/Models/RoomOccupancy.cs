namespace ESPresense.Models;

/// <summary>
/// Fused per-room occupancy: CSI motion plus which tracked devices are plausibly responsible for it.
/// Only emitted for rooms with at least one CSI-mapped node.
/// </summary>
/// <param name="Who">Devices attributed to this room's current occupied episode.</param>
/// <param name="Unknown">True when the room is occupied but no tracked device is plausibly present.</param>
public sealed record RoomOccupancy(string RoomId, string? RoomName, bool Occupied, IReadOnlyList<string> Who, bool Unknown);
