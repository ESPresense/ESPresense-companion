using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Converters;

public class RoomConverter : JsonConverter<Room>
{
    public override bool CanConvert(Type typeToConvert) => typeof(Room).IsAssignableFrom(typeToConvert);

    public override Room Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new Room();

    public override void Write(Utf8JsonWriter writer, Room room, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", room.Id);
        writer.WriteString("floor", room.Floor.Id);
        writer.WriteEndObject();
    }
}