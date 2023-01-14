using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Models;

namespace ESPresense.Converters;

public class FloorConverter : JsonConverter<Floor>
{
    public override bool CanConvert(Type typeToConvert) => typeof(Floor).IsAssignableFrom(typeToConvert);

    public override Floor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new Floor();

    public override void Write(Utf8JsonWriter writer, Floor room, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", room.Id);
        writer.WriteString("name", room.Name);
        writer.WriteEndObject();
    }
}