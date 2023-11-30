using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ESPresense.Utils;

public static class SerializerSettings
{
    public static readonly JsonSerializerSettings NullIgnore = new() { DefaultValueHandling = DefaultValueHandling.Ignore, NullValueHandling = NullValueHandling.Ignore, ContractResolver = new CamelCasePropertyNamesContractResolver() };
}