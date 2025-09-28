using ESPresense.Models;
using MathNet.Spatial.Euclidean;

namespace ESPresense.Companion.Tests.Models;

public class DeviceAnchoringTests
{
    [Test]
    public void SetAnchor_ShouldApplyLocationAndMarkAnchored()
    {
        var device = new Device("dev-1", null, TimeSpan.FromSeconds(30));
        var anchorLocation = new Point3D(1.2, 3.4, 0.5);

        device.SetAnchor(new DeviceAnchor(anchorLocation, null, null));

        Assert.That(device.IsAnchored, Is.True);
        Assert.That(device.Anchor, Is.Not.Null);
        Assert.That(device.Location, Is.EqualTo(anchorLocation));
        Assert.That(device.BestScenario, Is.Null);
    }

    [Test]
    public void ClearingAnchor_ShouldRequireReevaluation()
    {
        var device = new Device("dev-2", null, TimeSpan.FromSeconds(30));

        device.SetAnchor(new DeviceAnchor(new Point3D(1, 1, 1), null, null));
        device.Check = false;

        device.SetAnchor(null);

        Assert.That(device.IsAnchored, Is.False);
        Assert.That(device.Anchor, Is.Null);
        Assert.That(device.Check, Is.True);
    }
}
