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
    private static string Nsj(object value) => JsonConvert.SerializeObject(value, SerializerSettings.NullIgnore);

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
