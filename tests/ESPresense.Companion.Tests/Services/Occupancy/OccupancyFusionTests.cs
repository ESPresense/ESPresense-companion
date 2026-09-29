using ESPresense.Services.Occupancy;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services.Occupancy;

public class OccupancyFusionTests
{
    [Test]
    public void Fuse_BleDevicePresent_WinsOverCsi()
    {
        var fusion = new OccupancyFusion();
        var lowConfidenceCsi = new CsiOccupancySignal("node-1", DateTimeOffset.UtcNow, Variance: 1.0, Confidence: 0.05, WindowFrameCount: 1);

        var result = fusion.Fuse("living-room", bleDevicePresent: true, lowConfidenceCsi);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(1.0));
        Assert.That(result.Source, Is.EqualTo(OccupancySource.Ble));
    }

    [Test]
    public void Fuse_NoBle_HighConfidenceCsi_MarksOccupied()
    {
        var fusion = new OccupancyFusion();
        var confidentCsi = new CsiOccupancySignal("node-1", DateTimeOffset.UtcNow, Variance: 20.0, Confidence: 0.9, WindowFrameCount: 12);

        var result = fusion.Fuse("living-room", bleDevicePresent: false, confidentCsi);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(0.9));
        Assert.That(result.Source, Is.EqualTo(OccupancySource.Csi));
    }

    [Test]
    public void Fuse_NoBle_LowConfidenceCsi_MarksVacant()
    {
        var fusion = new OccupancyFusion();
        var quietCsi = new CsiOccupancySignal("node-1", DateTimeOffset.UtcNow, Variance: 2.0, Confidence: 0.1, WindowFrameCount: 12);

        var result = fusion.Fuse("living-room", bleDevicePresent: false, quietCsi);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Source, Is.EqualTo(OccupancySource.Csi));
    }

    [Test]
    public void Fuse_NoBleNoCsi_MarksVacantWithNoSource()
    {
        var fusion = new OccupancyFusion();

        var result = fusion.Fuse("living-room", bleDevicePresent: false, csi: null);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Confidence, Is.EqualTo(0.0));
        Assert.That(result.Source, Is.EqualTo(OccupancySource.None));
    }
}
