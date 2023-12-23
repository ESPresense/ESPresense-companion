using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Converters;

public class TimeSpanMillisConverter : JsonConverter<TimeSpan>
{
    public override bool CanConvert(Type typeToConvert) => typeof(TimeSpan).IsAssignableFrom(typeToConvert);

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new TimeSpan();

    public override void Write(Utf8JsonWriter writer, TimeSpan ts, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(ts.TotalMilliseconds);
    }
}