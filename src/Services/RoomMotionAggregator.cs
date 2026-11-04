using ESPresense.Models;
using Microsoft.Extensions.Logging;

namespace ESPresense.Services;

/// <summary>A room's motion read-out, aggregated from every CSI-mapped node inside it.</summary>
public readonly record struct RoomMotion(string RoomId, double MaxScore, bool Motion, IReadOnlyList<string> NodeIds);

/// <summary>
/// Combines <see cref="NodeRoomResolver"/> and <see cref="CsiMotionTracker"/> into one motion read-out
/// per room. Only rooms that resolve from at least one node appear in the result, so a room with no CSI
/// coverage is simply absent rather than reported as "no motion" -- the fusion engine needs that
/// distinction to leave BLE-only rooms untouched.
/// </summary>
public static class RoomMotionAggregator
{
    public static Dictionary<string, RoomMotion> Compute(State state, CsiMotionTracker tracker, ILogger? logger = null)
    {
        var byRoom = new Dictionary<string, (double MaxScore, bool Motion, List<string> Nodes)>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in state.Nodes.Values)
        {
            var room = NodeRoomResolver.Resolve(node);
            if (room?.Id == null)
            {
                logger?.LogDebug("CSI: node {NodeId} does not resolve to any room, skipping", node.Id);
                continue;
            }

            var motion = tracker.GetMotion(node.Id);
            if (motion == null) continue; // node has never reported csi_variance

            if (!byRoom.TryGetValue(room.Id, out var agg))
                agg = (0, false, new List<string>());

            agg.Nodes.Add(node.Id);
            if (motion.Value.Score > agg.MaxScore) agg.MaxScore = motion.Value.Score;
            agg.Motion |= motion.Value.IsMotion;
            byRoom[room.Id] = agg;
        }

        return byRoom.ToDictionary(
            kv => kv.Key,
            kv => new RoomMotion(kv.Key, kv.Value.MaxScore, kv.Value.Motion, kv.Value.Nodes),
            StringComparer.OrdinalIgnoreCase);
    }
}
