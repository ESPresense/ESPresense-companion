using System.Collections;
using System.Reflection;
using ESPresense.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ESPresense.Companion.Tests.Models;

/// <summary>
/// The Config.Clone() family is hand-written, so a property added to a config class is silently dropped from
/// copies unless its Clone() is updated too. These tests catch that by construction: one fills every settable
/// property in the graph by reflection, the other round-trips the shipped example config.
/// </summary>
public class ConfigCloneRoundTripTests
{
    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer YamlIn = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static string ExampleYaml()
    {
        var asm = typeof(Config).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("config.example.yaml", StringComparison.Ordinal));
        using var sr = new StreamReader(asm.GetManifestResourceStream(name)!);
        return sr.ReadToEnd();
    }

    [Test]
    public void ExampleConfig_CloneSerializesIdentically()
    {
        var original = YamlIn.Deserialize<Config>(ExampleYaml());
        var clone = original.Clone();

        Assert.That(Yaml.Serialize(clone), Is.EqualTo(Yaml.Serialize(original)));
    }

    [Test]
    public void ExampleConfig_CloneIsDeep()
    {
        var original = YamlIn.Deserialize<Config>(ExampleYaml());
        AssertDeepCopy(original, original.Clone(), "config");
    }

    [Test]
    public void NullFilteringSection_ClonesAsNull()
    {
        // "filtering: null" in config.yaml leaves the section null; consumers fall back to defaults.
        var original = YamlIn.Deserialize<Config>("filtering:\n");
        Assert.That(original.Filtering, Is.Null, "precondition: YAML leaves an empty section null");

        var clone = original.Clone();

        Assert.That(clone.Filtering, Is.Null);
    }

    [Test]
    public void EverySettableProperty_IsCopied()
    {
        var counter = 0;
        var original = (Config)Fill(typeof(Config), ref counter, depth: 0)!;
        var clone = original.Clone();

        AssertDeepCopy(original, clone, "config");
        Assert.That(Yaml.Serialize(clone), Is.EqualTo(Yaml.Serialize(original)));
    }

    [Test]
    public void ConfigMqtt_EverySettablePropertyIsCopied()
    {
        // The one Clone() production code calls directly (MqttCoordinator).
        var counter = 0;
        var original = (ConfigMqtt)Fill(typeof(ConfigMqtt), ref counter, depth: 0)!;
        AssertDeepCopy(original, original.Clone(), "mqtt");
    }

    private static IEnumerable<PropertyInfo> Settable(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.SetMethod!.IsPublic);

    /// <summary>Builds a <paramref name="type"/> with every settable property set to a value distinct from its default and from every other property.</summary>
    private static object? Fill(Type type, ref int n, int depth)
    {
        n++;
        if (type == typeof(string)) return $"v{n}";
        if (type == typeof(int) || type == typeof(int?)) return 1000 + n;
        if (type == typeof(double) || type == typeof(double?)) return 1000.25 + n;
        if (type == typeof(bool?)) return true;
        if (type.IsArray)
        {
            var el = type.GetElementType()!;
            var arr = Array.CreateInstance(el, 2);
            for (var i = 0; i < 2; i++) arr.SetValue(Fill(el, ref n, depth + 1), i);
            return arr;
        }
        if (type == typeof(Dictionary<string, double>))
            return new Dictionary<string, double> { [$"k{n}"] = n + 0.5, [$"k{n}b"] = n + 1.5 };

        Assert.That(depth, Is.LessThan(10), $"unexpected recursion filling {type}");
        Assert.That(type.IsClass, $"don't know how to fill {type}; extend {nameof(Fill)}");
        var obj = Activator.CreateInstance(type)!;
        foreach (var p in Settable(type))
        {
            if (p.PropertyType == typeof(bool))
                p.SetValue(obj, !(bool)p.GetValue(obj)!); // flip whatever the default is
            else
                p.SetValue(obj, Fill(p.PropertyType, ref n, depth + 1));
        }
        return obj;
    }

    private static void AssertDeepCopy(object? original, object? clone, string path)
    {
        if (original == null)
        {
            Assert.That(clone, Is.Null, path);
            return;
        }
        Assert.That(clone, Is.Not.Null, path);
        var type = original.GetType();
        Assert.That(clone!.GetType(), Is.EqualTo(type), path);

        if (type.IsValueType || type == typeof(string))
        {
            Assert.That(clone, Is.EqualTo(original), path);
            return;
        }

        Assert.That(ReferenceEquals(original, clone), Is.False, $"{path} is shared between the original and the clone");

        if (original is IDictionary od)
        {
            var cd = (IDictionary)clone;
            Assert.That(cd.Count, Is.EqualTo(od.Count), $"{path}.Count");
            foreach (DictionaryEntry e in od)
                AssertDeepCopy(e.Value, cd[e.Key], $"{path}[{e.Key}]");
            return;
        }

        if (original is Array oa)
        {
            var ca = (Array)clone;
            Assert.That(ca.Length, Is.EqualTo(oa.Length), $"{path}.Length");
            for (var i = 0; i < oa.Length; i++)
                AssertDeepCopy(oa.GetValue(i), ca.GetValue(i), $"{path}[{i}]");
            return;
        }

        foreach (var p in Settable(type))
            AssertDeepCopy(p.GetValue(original), p.GetValue(clone), $"{path}.{p.Name}");
    }
}
