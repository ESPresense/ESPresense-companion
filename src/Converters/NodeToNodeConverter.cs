using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ESPresense.Extensions;
using ESPresense.Models;

namespace ESPresense.Converters;

public class NodeToNodeConverter : JsonConverter<ConcurrentDictionary<string, NodeToNode>>
{
    private static readonly JsonConverter<IDictionary<string, object>> DefaultDictConverter =
        (JsonConverter<IDictionary<string, object>>)JsonSerializerOptions.Default.GetConverter(typeof(IDictionary<string, object>));

    public override ConcurrentDictionary<string, NodeToNode> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, ConcurrentDictionary<string, NodeToNode> distances, JsonSerializerOptions options)
    {
        var d = distances.Where(a => a.Value.Current).ToDictionary(
            a => a.Key,
            a => new { dist = a.Value.Distance, var = a.Value.Variance, lh = a.Value.LastHit.RelativeMilliseconds() } as object
        );

        DefaultDictConverter.Write(writer, d, options);
    }
}