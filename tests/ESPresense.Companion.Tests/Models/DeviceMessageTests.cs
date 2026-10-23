using ESPresense.Models;
using Newtonsoft.Json;

namespace ESPresense.Companion.Tests.Models;

[TestFixture]
public class DeviceMessageTests
{
    [Test]
    public void V5Report_DerivesRxAdjFromRawRssi()
    {
        // v5: rssi = raw + rx_offset_rssi, and no rxAdj. rxAdj (subtracted) = raw - rssi = -offset.
        var m = JsonConvert.DeserializeObject<DeviceMessage>("""{"rssi":-64.0,"rawRssi":-65.0,"distance":1.2}""")!;
        Assert.That(m.RawRssi, Is.EqualTo(-65.0));
        Assert.That(m.RssiRxAdj, Is.EqualTo(-1.0));
    }

    [Test]
    public void OlderReport_KeepsItsRxAdj()
    {
        var m = JsonConvert.DeserializeObject<DeviceMessage>("""{"rssi":-71.0,"rxAdj":5,"distance":3}""")!;
        Assert.That(m.RawRssi, Is.Null);
        Assert.That(m.RssiRxAdj, Is.EqualTo(5.0));
    }

    [Test]
    public void ReportWithoutEither_HasNoRxAdj()
    {
        var m = JsonConvert.DeserializeObject<DeviceMessage>("""{"rssi":-71.0,"distance":3}""")!;
        Assert.That(m.RssiRxAdj, Is.Null);
    }

    [Test]
    public void V5Report_CandidateAdjustmentAppliesToTheRawReading()
    {
        // The optimizers' rssi + rxAdj - candidate must give raw - candidate for a v5 report too.
        var m = JsonConvert.DeserializeObject<DeviceMessage>("""{"rssi":-64.0,"rawRssi":-65.0}""")!;
        var measure = new Measure { Rssi = m.Rssi, RssiRxAdj = m.RssiRxAdj };
        Assert.That(measure.GetAdjustedRssi(2), Is.EqualTo(-67.0));
    }
}
