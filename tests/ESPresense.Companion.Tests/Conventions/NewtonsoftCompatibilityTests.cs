using System.Text;
using System.Text.Json;
using ESPresense.Models;
using ESPresense.Utils;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests.Conventions;

/// <summary>
/// Differential check for #1846: the System.Text.Json options that replaced Newtonsoft on the MQTT boundary
/// produce exactly what Newtonsoft does, over every BMP character, a spread of doubles and each DateTimeKind.
/// </summary>
[TestFixture]
public class NewtonsoftCompatibilityTests
{
    private static string Stj(object value, JsonSerializerOptions options) => System.Text.Json.JsonSerializer.Serialize(value, options);
    // The Newtonsoft settings the MQTT boundary used before #1846 (formerly SerializerSettings.NullIgnore)
    private static readonly JsonSerializerSettings NullIgnore = new()
    {
        DefaultValueHandling = DefaultValueHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    private static string Nsj(object value) => JsonConvert.SerializeObject(value, NullIgnore);

    [Test]
    public void EveryBmpCharacter_IsEscapedLikeNewtonsoft()
    {
        var mismatches = new List<string>();
        for (var c = 0; c <= 0xFFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF) continue; // lone surrogates are not valid text
            var ds = new DeviceSettings { Id = "a" + (char)c + "b" };
            var expected = Nsj(ds);
            var actual = Stj(ds, JsonDefaults.Mqtt);
            if (expected != actual) mismatches.Add($"U+{c:X4}: {expected} vs {actual}");
        }
        Assert.That(mismatches, Is.Empty);
    }

    [TestCase("🙂")]
    [TestCase("𝄞 music")]
    [TestCase("家族の🏠")]
    [TestCase("")]
    public void SupplementaryAndMixedText_IsWrittenLikeNewtonsoft(string text)
    {
        var ds = new DeviceSettings { Id = text, Name = new StringBuilder().Append(text).Append("\u2028\u0085\u007f").ToString() };
        Assert.That(Stj(ds, JsonDefaults.Mqtt), Is.EqualTo(Nsj(ds)));
    }

    [TestCase("\ud800", TestName = "lone high surrogate alone")]
    [TestCase("a\ud800b", TestName = "lone high surrogate inside")]
    [TestCase("a\udc00b", TestName = "lone low surrogate inside")]
    [TestCase("ab\ud83d", TestName = "high surrogate at the end")]
    [TestCase("\udc00ab", TestName = "low surrogate at the start")]
    [TestCase("\ud83d\ud83d\ude42", TestName = "stray high before a valid pair")]
    public void LoneSurrogates_AreWrittenLikeNewtonsoft_AndNothingIsLost(string text)
    {
        // A lone surrogate can't survive UTF-8, which is what MQTT carries: both serializers must end up with
        // U+FFFD there and still write every member that follows.
        var ds = new DeviceSettings { Id = text, Name = "after", RefRssi = -59 };
        var expected = Encoding.UTF8.GetBytes(Nsj(ds));
        var actual = Encoding.UTF8.GetBytes(Stj(ds, JsonDefaults.Mqtt));
        Assert.That(Encoding.UTF8.GetString(actual), Is.EqualTo(Encoding.UTF8.GetString(expected)));
        Assert.That(Encoding.UTF8.GetString(actual), Does.Contain("\"name\":\"after\",\"rssi@1m\":-59"));
    }

    [Test]
    public void Doubles_AreFormattedLikeNewtonsoft()
    {
        var values = new List<double>
        {
            0.1, -0.0, 1, -1, 2.5, 1e15, 1e16, 1e17, 1e21, 1e-5, 1e-7, -1e-6, 123456789012345678, 0.30000000000000004,
            double.MaxValue, double.MinValue, double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity
        };
        var random = new Random(1846);
        for (var i = 0; i < 2000; i++)
        {
            values.Add((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-12, 22)));
            values.Add(Math.Round((random.NextDouble() - 0.5) * 40, random.Next(0, 4)));
        }

        var mismatches = values
            .Select(v => new DeviceSettings { X = v })
            .Select(ds => (Expected: Nsj(ds), Actual: Stj(ds, JsonDefaults.Mqtt)))
            .Where(p => p.Expected != p.Actual)
            .Select(p => $"{p.Expected} vs {p.Actual}")
            .ToList();
        Assert.That(mismatches, Is.Empty);
    }

    [TestCase(DateTimeKind.Utc)]
    [TestCase(DateTimeKind.Local)]
    [TestCase(DateTimeKind.Unspecified)]
    public void DateTimes_AreFormattedLikeNewtonsoft(DateTimeKind kind)
    {
        foreach (var ticks in new long[] { 0, 1, 10, 1_000_000, 1_234_567, 9_999_999 })
        {
            var value = new { last_seen = new DateTime(2026, 6, 15, 12, 30, 45, kind).AddTicks(ticks) };
            Assert.That(Stj(value, JsonDefaults.Verbatim), Is.EqualTo(JsonConvert.SerializeObject(value)), $"ticks {ticks}");
        }
    }
}
