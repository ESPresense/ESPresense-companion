using System.Text.Json;
using System.Text.Json.Serialization;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Converters;

public class Point3DConverter : JsonConverter<Point3D>
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(Point3D).IsAssignableFrom(typeToConvert);

    public override Point3D Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException();
        }

        string? propertyName = reader.GetString();
        if (propertyName != "TypeDiscriminator")
        {
            throw new JsonException();
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException();
        }

        double x = 0, y = 0, z = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new Point3D(x, y, z);
            }

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                propertyName = reader.GetString();
                reader.Read();
                switch (propertyName)
                {
                    case "x":
                        x = reader.GetDouble();
                        break;
                    case "y":
                        y = reader.GetDouble();
                        break;
                    case "z":
                        z = reader.GetDouble();
                        break;
                }
            }
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Point3D p, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteNumber("x", Math.Round(p.X, 3));
        writer.WriteNumber("y", Math.Round(p.Y, 3));
        writer.WriteNumber("z", Math.Round(p.Z, 3));

        writer.WriteEndObject();
    }
}