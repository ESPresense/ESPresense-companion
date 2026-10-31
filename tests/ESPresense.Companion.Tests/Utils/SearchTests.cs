using ESPresense.Models;
using ESPresense.Utils;

namespace ESPresense.Companion.Tests.Utils;

public class SearchTests
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
        Search.Find(devices.Select(d => Search.Device(d, d.Name)), q, limit).Select(r => r.Id).ToList();

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

        var result = Search.Find([Search.Device(device, device.Name)], "a4c138", 20).Single();

        Assert.That(result.Kind, Is.EqualTo("device"));
        Assert.That(result.Detail, Is.EqualTo("3 nodes · nearest nook 2.63 m"));
        Assert.That(result.Badge, Is.EqualTo("untracked"));
    }

    [Test]
    public void UsesTheResolvedNameForMatchingAndOutput()
    {
        var device = MakeDevice("a4c138a4048c");

        var result = Search.Find([Search.Device(device, "Dryer vent")], "dryer", 20).Single();

        Assert.That(result.Name, Is.EqualTo("Dryer vent"));
    }

    [Test]
    public void FullMacTypedWithSeparatorsRanksAsExact()
    {
        var full = MakeDevice("a4c138a4048c");
        var longer = MakeDevice("a4c138a4048c99", null, true);

        var result = Ids(new[] { longer, full }, "a4:c1:38:a4:04:8c");

        Assert.That(result, Is.EqualTo(new[] { "a4c138a4048c", "a4c138a4048c99" }));
    }

    [Test]
    public void IgnoresNonFiniteDistancesWhenPickingNearest()
    {
        var device = MakeDevice("a4c138a4048c", null, false, ("bad", double.NaN), ("kitchen", 2.97));

        var result = Search.Find([Search.Device(device, device.Name)], "a4c138", 20).Single();

        Assert.That(result.Detail, Is.EqualTo("2 nodes · nearest kitchen 2.97 m"));
    }

    [Test]
    public void DeviceNobodyHearsSaysSo()
    {
        var result = Search.Find([Search.Device(MakeDevice("a4c138a4048c", null, true), null)], "", 20).Single();

        Assert.That(result.Detail, Is.EqualTo("not heard recently"));
        Assert.That(result.Badge, Is.Null);
    }

    [Test]
    public void FindsNodesByIdNameOrFloor()
    {
        var node = Search.Node(new NodeStateTele { Id = "kitchen", Name = "Kitchen", Floors = ["ground"], Online = true });

        var result = Search.Find([node], "ground", 20).Single();

        Assert.That(result.Kind, Is.EqualTo("node"));
        Assert.That(result.Detail, Is.EqualTo("online"));
        Assert.That(Search.Find([node], "attic", 20), Is.Empty);
    }

    [Test]
    public void RanksAcrossKinds_NodesFirstOnlyWithinATier()
    {
        var node = Search.Node(new NodeStateTele { Id = "kitchen-2", Name = null });
        var exact = Search.Device(MakeDevice("kitchen"), null);
        var prefix = Search.Device(MakeDevice("kitchen-speaker", null, true), null);

        var result = Search.Find([prefix, node, exact], "kitchen", 20).Select(r => r.Id);

        Assert.That(result, Is.EqualTo(new[] { "kitchen", "kitchen-2", "kitchen-speaker" }));
    }
}
