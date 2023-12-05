using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Models;

namespace ESPresense.Converters;

public class NodeDistanceConverter : JsonConverter<ConcurrentDictionary<string, DeviceNode>>
{
    private static readonly JsonConverter<IDictionary<string, object>> DefaultDictConverter =
        (JsonConverter<IDictionary<string, object>>)JsonSerializerOptions.Default.GetConverter(typeof(IDictionary<string, object>));

    public override ConcurrentDictionary<string, DeviceNode>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Your existing read implementation or throw if not needed.
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, ConcurrentDictionary<string, DeviceNode> distances, JsonSerializerOptions options)
    {
        // Creating a dictionary with both dist and var
        var d = distances.Where(a => a.Value.Current).ToDictionary(
            a => a.Key,
            a => new { dist = a.Value.Distance, var = a.Value.Variance } as object
        );

        // Using the dictionary converter to write this new structure
        DefaultDictConverter.Write(writer, d, options);
    }
}
