using Newtonsoft.Json;

namespace ESPresense.Extensions
{
    public static class DictionaryExtensions
    {
        public static T GetOrAdd<T>(this IDictionary<string, T> dict, string key, Func<string, T>? factory = null) where T : new()
        {
            if (dict.TryGetValue(key, out var value)) return value;
            value = factory == null ? new() ! : factory(key);
            dict.Add(key, value);
            return value;
        }   
    }
}
