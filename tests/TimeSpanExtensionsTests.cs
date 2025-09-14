using System;
using ESPresense.Extensions;
using NUnit.Framework;

namespace ESPresense.Companion.Tests;

public class TimeSpanExtensionsTests
{
    [Test]
    public void TryParseDurationString_ValidCompound_ReturnsExpectedTimeSpan()
    {
        var result = "1h30m".TryParseDurationString(out var ts);
        Assert.IsTrue(result);
        Assert.That(ts, Is.EqualTo(TimeSpan.FromMinutes(90)));
    }

    [Test]
    public void TryParseDurationString_ValidMixedUnits_ReturnsExpectedTimeSpan()
    {
        var result = "1d2h3m4s".TryParseDurationString(out var ts);
        Assert.IsTrue(result);
        Assert.That(ts, Is.EqualTo(new TimeSpan(1, 2, 3, 4)));
    }

    [Test]
    public void TryParseDurationString_InvalidString_ReturnsFalse()
    {
        var result = "1h30x".TryParseDurationString(out var ts);
        Assert.IsFalse(result);
        Assert.That(ts, Is.EqualTo(default(TimeSpan)));
    }

    [TestCase("30s", 30, 0, 0, 0)]
    [TestCase("5m", 0, 5, 0, 0)]
    [TestCase("2h", 0, 0, 2, 0)]
    [TestCase("7d", 0, 0, 0, 7)]
    public void TryParseDurationString_SimpleUnits_ReturnsCorrectTimeSpan(string input, int seconds, int minutes, int hours, int days)
    {
        Assert.IsTrue(input.TryParseDurationString(out var result));
        var expected = new TimeSpan(days, hours, minutes, seconds);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("120", DurationUnit.Seconds, 120)]
    [TestCase("30", DurationUnit.Days, 2592000)] // 30 days in seconds
    [TestCase("48", DurationUnit.Hours, 172800)] // 48 hours in seconds  
    [TestCase("90", DurationUnit.Minutes, 5400)] // 90 minutes in seconds
    public void TryParseDurationString_DefaultUnits_ReturnsCorrectTimeSpan(string input, DurationUnit defaultUnit, int expectedSeconds)
    {
        Assert.IsTrue(input.TryParseDurationString(out var result, defaultUnit));
        Assert.That(result, Is.EqualTo(TimeSpan.FromSeconds(expectedSeconds)));
    }

    [Test]
    public void TryParseDurationString_DefaultUnitBackwardCompatibility_ReturnsCorrectTimeSpan()
    {
        Assert.IsTrue("120".TryParseDurationString(out var result));
        Assert.That(result, Is.EqualTo(TimeSpan.FromSeconds(120)));
    }

    [TestCase("30s10")]
    [TestCase("1h30")]
    [TestCase("2d5")]
    [TestCase("5m120")]
    public void TryParseDurationString_MixedExplicitAndUnitless_ReturnsFalse(string input)
    {
        // These should fail because mixing explicit units with unitless numbers is ambiguous
        Assert.IsFalse(input.TryParseDurationString(out var _));
        Assert.IsFalse(input.TryParseDurationString(out var _, DurationUnit.Days));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]
    [TestCase("30x")] // Invalid unit
    [TestCase("s30")] // Unit before number
    [TestCase("30ss")] // Double unit
    public void TryParseDurationString_InvalidFormats_ReturnsFalse(string input)
    {
        Assert.IsFalse(input.TryParseDurationString(out var _));
    }

    [Test]
    public void TryParseDurationString_NullInput_ReturnsFalse()
    {
        Assert.IsFalse(((string)null).TryParseDurationString(out var _));
    }

    [TestCase("30S", 30, 0, 0, 0)]
    [TestCase("5M", 0, 5, 0, 0)]
    [TestCase("2H", 0, 0, 2, 0)]
    [TestCase("7D", 0, 0, 0, 7)]
    public void TryParseDurationString_CaseInsensitive_ReturnsCorrectTimeSpan(string input, int seconds, int minutes, int hours, int days)
    {
        Assert.IsTrue(input.TryParseDurationString(out var result));
        var expected = new TimeSpan(days, hours, minutes, seconds);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("0s")]
    [TestCase("0")]
    public void TryParseDurationString_ZeroValues_ReturnsZeroTimeSpan(string input)
    {
        Assert.IsTrue(input.TryParseDurationString(out var result));
        Assert.That(result, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void TryParseDurationString_ZeroWithDefaultUnit_ReturnsZeroTimeSpan()
    {
        Assert.IsTrue("0".TryParseDurationString(out var result, DurationUnit.Days));
        Assert.That(result, Is.EqualTo(TimeSpan.Zero));
    }

    [TestCase("365d", 365)]
    [TestCase("8760h", 8760)]
    public void TryParseDurationString_LargeNumbers_ReturnsCorrectTimeSpan(string input, int expectedValue)
    {
        Assert.IsTrue(input.TryParseDurationString(out var result));
        var expected = input.EndsWith('d') ? TimeSpan.FromDays(expectedValue) : TimeSpan.FromHours(expectedValue);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void TryParseDurationString_BackwardCompatibility_WorksAsExpected()
    {
        // Test that existing calls without defaultUnit parameter still work
        Assert.IsTrue("30".TryParseDurationString(out var result));
        Assert.That(result, Is.EqualTo(TimeSpan.FromSeconds(30))); // Should default to seconds

        Assert.IsTrue("1h30m".TryParseDurationString(out var result2));
        Assert.That(result2, Is.EqualTo(TimeSpan.FromMinutes(90)));
    }
}
