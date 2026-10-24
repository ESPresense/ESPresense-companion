using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Companion.Tests.Utils;

public class DeviceSearchTests
{
    private static Device MakeDevice(string id, string? name = null, bool track = false, params (string Node, double Dist)[] hits)
    {
        var device = new Device(id, null, TimeSpan.FromMinutes(5)) { Name = name, Track = track };
        foreach (var (nodeId, dist) in hits)
        {
            var node = new Node(nodeId, NodeSourceType.Discovered);
            device.Nodes[nodeId] = new DeviceToNode(device, node) { Distance = dist, LastHit = DateTime.UtcNow };
        }
        return device;
    }

    private static List<string> Ids(IEnumerable<Device> devices, string? q, int limit = 20) =>
        DeviceSearch.Search(devices, q, limit, d => d.Name).Select(r => r.Id).ToList();

    [Test]
    public void FindsAnUntrackedBareMacByPrefixOrSubstring()
    {
        var devices = new[] { MakeDevice("a4c138a4048c"), MakeDevice("tv:family", "Family TV", true) };

        Assert.That(Ids(devices, "a4c138"), Is.EqualTo(new[] { "a4c138a4048c" }));
        Assert.That(Ids(devices, "4048c"), Is.EqualTo(new[] { "a4c138a4048c" }));
    }

    [Test]
    public void MacMatchesWithOrWithoutSeparators()
    {
        var devices = new[] { MakeDevice("a4c138a4048c"), MakeDevice("b0b0b0b0b0b0") };

        Assert.That(Ids(devices, "a4:c1:38"), Is.EqualTo(new[] { "a4c138a4048c" }));
        Assert.That(Ids(devices, "A4-C1-38-A4"), Is.EqualTo(new[] { "a4c138a4048c" }));
    }

    [Test]
    public void TermsAreAndedAcrossIdNameAndRoom()
    {
        var keys = MakeDevice("keys:darrell", "Darrell's Keys", true);
        var tv = MakeDevice("tv:family", "Family TV", true);
        var devices = new[] { keys, tv };

        Assert.That(Ids(devices, "keys darrell"), Is.EqualTo(new[] { "keys:darrell" }));
        Assert.That(Ids(devices, "keys tv"), Is.Empty);
    }

    [Test]
    public void RanksExactThenPrefixThenSubstring_AndTrackedBeforeUntrackedWithinATier()
    {
        var substring = MakeDevice("xx-tv-xx", null, true);
        var prefixUntracked = MakeDevice("tv:a");
        var prefixTracked = MakeDevice("tv:b", null, true);
        var exact = MakeDevice("tv");

        var result = Ids(new[] { substring, prefixUntracked, prefixTracked, exact }, "tv");

        Assert.That(result, Is.EqualTo(new[] { "tv", "tv:b", "tv:a", "xx-tv-xx" }));
    }

    [Test]
    public void CapsResultsAtLimit()
    {
        var devices = Enumerable.Range(0, 50).Select(i => MakeDevice($"dev{i:00}")).ToList();

        Assert.That(Ids(devices, "dev", 5), Has.Count.EqualTo(5));
    }

    [Test]
    public void EmptyQueryReturnsTrackedDevicesFirst()
    {
        var devices = new[] { MakeDevice("zz-untracked"), MakeDevice("aa-tracked", null, true) };

        Assert.That(Ids(devices, ""), Is.EqualTo(new[] { "aa-tracked", "zz-untracked" }));
    }

    [Test]
    public void ReportsNearestCurrentNodeAndNodeCount()
    {
        var device = MakeDevice("a4c138a4048c", null, false, ("kitchen", 2.97), ("nook", 2.63), ("laundry", 5.52));

        var result = DeviceSearch.Search(new[] { device }, "a4c138", 20, d => d.Name).Single();

        Assert.That(result.Nodes, Is.EqualTo(3));
        Assert.That(result.NearestNode, Is.EqualTo("nook"));
        Assert.That(result.NearestDistance, Is.EqualTo(2.63).Within(1e-9));
        Assert.That(result.Tracked, Is.False);
    }

    [Test]
    public void UsesTheResolvedNameForMatchingAndOutput()
    {
        var device = MakeDevice("a4c138a4048c");

        var result = DeviceSearch.Search(new[] { device }, "dryer", 20, _ => "Dryer vent").Single();

        Assert.That(result.Name, Is.EqualTo("Dryer vent"));
    }
}
