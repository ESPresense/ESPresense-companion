using ESPresense.Locators;
using ESPresense.Models;

namespace ESPresense.Companion.Tests.Locators;

[TestFixture]
public class FingerprintLocatorTests
{
    private static Fingerprint F(double x, double y, params (string Node, double Rssi)[] r) =>
        new() { Floor = "first", X = x, Y = y, Z = 0, Rssi = r.ToDictionary(a => a.Node, a => a.Rssi, StringComparer.OrdinalIgnoreCase) };

    private static readonly Fingerprint[] Map =
    {
        F(0, 0, ("a", -50), ("b", -70), ("c", -80), ("d", -75)),
        F(5, 0, ("a", -70), ("b", -50), ("c", -75), ("d", -80)),
        F(5, 5, ("a", -80), ("b", -75), ("c", -50), ("d", -70)),
        F(0, 5, ("a", -75), ("b", -80), ("c", -70), ("d", -50)),
    };

    [Test]
    public void ExactReadingLandsOnItsFingerprint()
    {
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -70, ["b"] = -50, ["c"] = -75, ["d"] = -80 }, Map, k: 1, minCommon: 3)!;
        Assert.That((m.Location.X, m.Location.Y), Is.EqualTo((5.0, 0.0)));
        Assert.That(m.Residual, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void ConstantOffsetIsIgnored()
    {
        // A device transmitting 12 dB hotter than the survey beacon still matches the same spot.
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -68, ["b"] = -63, ["c"] = -38, ["d"] = -58 }, Map, k: 1, minCommon: 3)!;
        Assert.That((m.Location.X, m.Location.Y), Is.EqualTo((5.0, 5.0)));
        Assert.That(m.Residual, Is.EqualTo(0).Within(1e-9));
    }

    [Test]
    public void TooFewSharedNodesIsNoMatch()
    {
        Assert.That(FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -50, ["x"] = -60 }, Map, k: 3, minCommon: 3), Is.Null);
    }

    [Test]
    public void KNearestAreAveragedTowardsTheBestMatch()
    {
        // Halfway between the two left-hand spots, slightly nearer (0,0).
        var m = FingerprintLocator.Match(new Dictionary<string, double> { ["a"] = -60, ["b"] = -76, ["c"] = -76, ["d"] = -64 }, Map, k: 2, minCommon: 3)!;
        Assert.Multiple(() =>
        {
            Assert.That(m.Location.X, Is.EqualTo(0).Within(1e-9));
            Assert.That(m.Location.Y, Is.GreaterThan(0).And.LessThan(5));
        });
    }

    [Test]
    public void SilentNodeCountsAgainstASpotWhereItWasLoud()
    {
        // Both spots match a, b, c exactly; only the second was right next to node e, which the device
        // does not hear. Shared nodes alone cannot tell them apart; a missing-node rssi can.
        var spots = new[]
        {
            F(0, 0, ("a", -60), ("b", -70), ("c", -80)),
            F(9, 9, ("a", -60), ("b", -70), ("c", -80), ("e", -45)),
        };
        var reading = new Dictionary<string, double> { ["a"] = -60, ["b"] = -70, ["c"] = -80 };
        var withFloor = FingerprintLocator.Match(reading, spots.Reverse(), k: 1, minCommon: 3, missingRssi: -100)!;
        Assert.That((withFloor.Location.X, withFloor.Location.Y), Is.EqualTo((0.0, 0.0)));
    }

    [Test]
    public void MapFileIsReadCaseInsensitivelyAndReloadedWhenChanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fp-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """{"fingerprints":[{"floor":"First","x":1,"y":2,"z":0,"rssi":{"Node_A":-60}}]}""");
            var map = new FingerprintMap(path);
            Assert.That(map.ForFloor("first").Single().Rssi["node_a"], Is.EqualTo(-60));

            File.WriteAllText(path, """{"fingerprints":[]}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.That(map.All, Is.Empty);
        }
        finally { File.Delete(path); }
    }
}
