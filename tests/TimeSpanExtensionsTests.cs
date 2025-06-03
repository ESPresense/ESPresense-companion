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
}
