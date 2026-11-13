using System.Collections;
using System.Reflection;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Services;

/// <summary>
/// Finds the keys in config.yaml that no setting reads. The loader ignores unmatched properties so that one typo
/// cannot stop the whole config loading, but then a misspelt key, a setting from a newer or older version, or one a
/// beta added and a later release dropped, is silently without effect. Listing them lets the loader say so.
/// </summary>
internal static class ConfigKeyCheck
{
    /// <summary>Dotted paths of keys in <paramref name="yaml"/> that no property of <typeparamref name="T"/> reads, e.g. <c>optimization.judgee</c> or <c>nodes[2].pointt</c>.</summary>
    public static List<string> UnknownKeys<T>(string yaml)
    {
        var unknown = new List<string>();
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (Exception) { return unknown; } // the loader reports syntax errors itself
        if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
            Walk(root, typeof(T), "", unknown);
        return unknown;
    }

    private static void Walk(YamlMappingNode node, Type type, string path, List<string> unknown)
    {
        var keys = Keys(type);
        foreach (var (k, v) in node.Children)
        {
            if (k is not YamlScalarNode { Value: { } key }) continue;
            var here = path.Length == 0 ? key : $"{path}.{key}";
            if (!keys.TryGetValue(key, out var propType)) { unknown.Add(here); continue; }
            Descend(v, propType, here, unknown);
        }
    }

    private static void Descend(YamlNode value, Type type, string path, List<string> unknown)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (IsFreeForm(type)) return;
        if (value is YamlMappingNode map && IsSettingsClass(type))
            Walk(map, type, path, unknown);
        else if (value is YamlSequenceNode seq && ElementType(type) is { } element && IsSettingsClass(element))
        {
            var i = 0;
            foreach (var item in seq.Children)
            {
                if (item is YamlMappingNode m) Walk(m, element, $"{path}[{i}]", unknown);
                i++;
            }
        }
    }

    /// <summary>Each YAML key a type reads, with the property type: the YamlMember alias, else the underscored name.</summary>
    private static Dictionary<string, Type> Keys(Type type)
    {
        var keys = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetCustomAttribute<YamlIgnoreAttribute>() != null || p.GetIndexParameters().Length > 0) continue;
            var alias = p.GetCustomAttribute<YamlMemberAttribute>()?.Alias;
            keys[alias ?? UnderscoredNamingConvention.Instance.Apply(p.Name)] = p.PropertyType;
        }
        return keys;
    }

    private static bool IsFreeForm(Type t) =>
        typeof(IDictionary).IsAssignableFrom(t) ||
        t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) ||
        t == typeof(object);

    private static bool IsSettingsClass(Type t) =>
        t.IsClass && t != typeof(string) && !t.IsArray && !typeof(IEnumerable).IsAssignableFrom(t);

    private static Type? ElementType(Type t) =>
        t.IsArray ? t.GetElementType()
        : t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
}
