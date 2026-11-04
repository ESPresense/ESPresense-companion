using System.Text.Json;
using ESPresense.Models;

namespace ESPresense.Services;

/// <summary>
/// Publishes a Home Assistant MQTT-discovery <c>binary_sensor</c> per room for fused occupancy, with
/// <c>who</c>/<c>unknown</c> as JSON attributes. Follows the retained discovery + state + attributes
/// topic shape used elsewhere in this codebase (e.g. device_tracker discovery in AutoDiscovery.cs).
/// </summary>
public static class RoomOccupancyDiscovery
{
    public static string DiscoveryTopic(IMqttCoordinator mqtt, string roomId) =>
        $"{mqtt.DiscoveryTopic}/binary_sensor/espresense-room-{roomId}/config";

    public static string StateTopic(string roomId) => $"espresense/companion/room/{roomId}/occupancy";

    public static string AttributesTopic(string roomId) => $"{StateTopic(roomId)}/attributes";

    public static async Task Publish(IMqttCoordinator mqtt, RoomOccupancy occupancy)
    {
        var discoveryPayload = JsonSerializer.Serialize(new
        {
            name = $"{occupancy.RoomName ?? occupancy.RoomId} Occupancy",
            unique_id = $"espresense-companion-room-{occupancy.RoomId}-occupancy",
            device_class = "occupancy",
            state_topic = StateTopic(occupancy.RoomId),
            json_attributes_topic = AttributesTopic(occupancy.RoomId),
            availability_topic = "espresense/companion/status",
            payload_on = "ON",
            payload_off = "OFF",
            device = new { identifiers = new[] { "espresense-companion-rooms" }, name = "ESPresense Companion", manufacturer = "ESPresense" },
            origin = new { name = "ESPresense Companion" }
        });

        await mqtt.TryEnqueueAsync(DiscoveryTopic(mqtt, occupancy.RoomId), discoveryPayload, true);
        await mqtt.TryEnqueueAsync(StateTopic(occupancy.RoomId), occupancy.Occupied ? "ON" : "OFF", true);
        await mqtt.TryEnqueueAsync(AttributesTopic(occupancy.RoomId), JsonSerializer.Serialize(new
        {
            who = occupancy.Who,
            unknown = occupancy.Unknown
        }), true);
    }
}
