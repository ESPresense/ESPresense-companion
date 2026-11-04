using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ESPresense.Simulation.FusionValidation;

/// <summary>
/// One fused per-room occupancy event, as proposed in the public design doc
/// (ESPresense/ESPresense-companion#1832, "Output" section): "occupied", "who: [ids]",
/// "unknown: bool" per room, over the existing WebSocket/REST paths.
///
/// PROVISIONAL: ESPA-462 (output: WebSocket event / REST / HA discovery entities) is
/// still `todo` as of this harness's first cut, so this shape has not been confirmed
/// against real code. Treat this record as the contract this harness expects; update
/// it — and the file-based LoadFromFile parser below — once ESPA-462 lands and defines
/// the actual wire format.
/// </summary>
public sealed record FusionRoomEvent(DateTimeOffset Timestamp, string Room, bool Occupied, IReadOnlyList<string> Who, bool Unknown);

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
}
