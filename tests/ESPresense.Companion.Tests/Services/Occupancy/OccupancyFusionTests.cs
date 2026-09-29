using ESPresense.Services.Occupancy;
using NUnit.Framework;

namespace ESPresense.Companion.Tests.Services.Occupancy;

// Mirrors occupancy_fusion.py's TestFusionRule (PR #1708 / ESPA-197 FUSION_DESIGN.md) branch
// for branch so the C# port can't silently drift from the validated design.
public class OccupancyFusionTests
{
    private const double Hit = 0.5;   // >= threshold
    private const double Quiet = 0.01; // < threshold

    [Test]
    public void Fuse_BlePresentAndCsiHit_AgreeOccupiedHighConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: true, csiScore: Hit);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.High));
    }

    [Test]
    public void Fuse_BlePresentAndCsiQuiet_TrustsBleMediumConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: true, csiScore: Quiet);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Medium));
    }

    [Test]
    public void Fuse_BlePresentAndCsiUnavailable_TrustsBleMediumConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: true, csiScore: null);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Medium));
    }

    [Test]
    public void Fuse_BleAbsentAndCsiHit_DeviceFreeOccupantMediumConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: false, csiScore: Hit);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Medium));
    }

    [Test]
    public void Fuse_BleAbsentAndCsiQuiet_AgreeVacantHighConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: false, csiScore: Quiet);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.High));
    }

    [Test]
    public void Fuse_BleAbsentAndCsiUnavailable_VacantMediumConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: false, csiScore: null);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Medium));
    }

    [Test]
    public void Fuse_BleUnavailableAndCsiHit_OccupiedLowConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: null, csiScore: Hit);

        Assert.That(result.IsOccupied, Is.True);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Low));
    }

    [Test]
    public void Fuse_BleUnavailableAndCsiUnavailable_VacantLowConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: null, csiScore: null);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Low));
    }

    [Test]
    public void Fuse_BleUnavailableAndCsiQuiet_VacantMediumConfidence()
    {
        var result = new OccupancyFusion().Fuse("living-room", blePresent: null, csiScore: Quiet);

        Assert.That(result.IsOccupied, Is.False);
        Assert.That(result.Confidence, Is.EqualTo(OccupancyConfidence.Medium));
    }
}
