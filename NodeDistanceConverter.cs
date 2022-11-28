using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Models;

namespace ESPresense;

public class NodeDistanceConverter : JsonConverter<ConcurrentDictionary<string, DeviceNode>>
{
    private static readonly JsonConverter<IDictionary<string, double>> DefaultDictConverter = 
        (JsonConverter<IDictionary<string, double>>)JsonSerializerOptions.Default.GetConverter(typeof(IDictionary<string, double>));

    public override ConcurrentDictionary<string, DeviceNode>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, ConcurrentDictionary<string, DeviceNode> distances, JsonSerializerOptions options)
    {
        var d = distances.ToDictionary(a => a.Key, a => a.Value.Distance);
        DefaultDictConverter.Write(writer, d, options);
    }
}