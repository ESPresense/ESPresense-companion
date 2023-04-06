namespace ESPresense.Extensions
{
    public static class DictionaryExtensions
    {
        public static TValue GetOrAdd<TValue, TKey>(this IDictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue>? factory = null) where TValue : new()
        {
            if (dict.TryGetValue(key, out var value)) return value;
            value = factory == null ? new TValue() ! : factory(key);
            dict.Add(key, value);
            return value;
        }
    }
}
