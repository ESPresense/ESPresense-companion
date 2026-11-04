using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// One fused per-room occupancy event: "occupied", "who: [ids]", "unknown: bool" per room, as emitted
/// by <see cref="ESPresense.Services.CsiFusionService"/> and published over the existing WebSocket/REST
/// paths (<c>roomOccupancyChanged</c> / <c>GET /api/state/rooms</c>) plus HA MQTT discovery.
///
/// This record's shape matches <see cref="ESPresense.Models.RoomOccupancy"/> field-for-field (see
/// <see cref="FromRoomOccupancy"/>) now that the real fusion output exists; it was provisional when this
/// harness was first written, ahead of the fusion engine itself.
/// </summary>
public sealed record FusionRoomEvent(DateTimeOffset Timestamp, string Room, bool Occupied, IReadOnlyList<string> Who, bool Unknown)
{
    public static FusionRoomEvent FromRoomOccupancy(DateTimeOffset timestamp, ESPresense.Models.RoomOccupancy occupancy) =>
        new(timestamp, occupancy.RoomId, occupancy.Occupied, occupancy.Who, occupancy.Unknown);
}

public static class FusionEventLog
{
    public static List<FusionRoomEvent> LoadFromFile(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonArray
            ?? throw new FormatException("expected a JSON array of fusion room events");

        var events = new List<FusionRoomEvent>();
        foreach (var node in root)
        {
            if (node is not JsonObject o) continue;
            var ts = DateTimeOffset.Parse(o["timestamp"]!.GetValue<string>());
            var room = o["room"]!.GetValue<string>();
            var occupied = o["occupied"]!.GetValue<bool>();
            var unknown = o["unknown"]?.GetValue<bool>() ?? false;
            var who = (o["who"] as JsonArray)?.Select(w => w!.GetValue<string>()).ToList() ?? new List<string>();
            events.Add(new FusionRoomEvent(ts, room, occupied, who, unknown));
        }
        return events.OrderBy(e => e.Timestamp).ToList();
    }

    public static void SaveToFile(string path, IEnumerable<FusionRoomEvent> events)
    {
        var root = new JsonArray();
        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            var who = new JsonArray();
            foreach (var id in e.Who) who.Add(id);
            root.Add(new JsonObject
            {
                ["timestamp"] = e.Timestamp.ToString("O"),
                ["room"] = e.Room,
                ["occupied"] = e.Occupied,
                ["who"] = who,
                ["unknown"] = e.Unknown
            });
        }
        File.WriteAllText(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
