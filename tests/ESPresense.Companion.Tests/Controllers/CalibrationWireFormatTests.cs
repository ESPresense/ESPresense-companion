using System.Text.Json;

namespace ESPresense.Companion.Tests.Controllers;

/// <summary>
/// Pins the exact JSON of GET /api/state/calibration (keys, key order, nesting, the anchored set,
/// R and RMSE) so refactoring the calibration builder cannot change the wire format.
/// </summary>
public class CalibrationWireFormatTests
{
    private CalibrationFixture _f = null!;

    [SetUp]
    public async Task Setup() => _f = await CalibrationFixture.CreateAsync();

    [TearDown]
    public async Task TearDown() => await _f.DisposeAsync();

    internal const string ExpectedRestJson = """
        {"rmse":0.39528470752104744,"r":0.9556189340787526,"bestRMSE":0.39528470752104744,"bestR":0.9556189340787526,"matrix":{"Alpha":{"Bravo":{"tx_ref_rssi":-59,"mapDistance":4,"distance":4.5,"rssi":-71,"diff":0.5,"percent":0.125,"var":0.25},"Charlie":{"tx_ref_rssi":-59,"rx_adj_rssi":-3,"mapDistance":3,"distance":2.5,"rssi":-66,"diff":-0.5,"percent":-0.16666666666666666}},"Bravo":{"Alpha":{"tx_ref_rssi":-61,"rx_adj_rssi":2,"absorption":2.7,"mapDistance":4,"distance":3.75,"rssi":-70,"diff":-0.25,"percent":-0.0625,"var":0.5},"Charlie":{"tx_ref_rssi":-61,"rx_adj_rssi":-3,"mapDistance":5,"distance":5.5,"rssi":-78,"diff":0.5,"percent":0.1,"var":"NaN"}},"Charlie":{"Alpha":{"rx_adj_rssi":2,"absorption":2.7,"mapDistance":3,"distance":3.25,"rssi":-64,"diff":0.25,"percent":0.08333333333333333,"var":0.125},"Bravo":{"mapDistance":5,"distance":4.75,"rssi":-77,"diff":-0.25,"percent":-0.05,"var":1}},"Anchor One":{"Alpha":{"rx_adj_rssi":2,"absorption":2.7,"mapDistance":2,"distance":2.25,"rssi":-60,"diff":0.25,"percent":0.125,"var":0.75},"Bravo":{"mapDistance":2,"distance":1.5,"rssi":-58,"diff":-0.5,"percent":-0.25}}},"optimizerState":{"optimizers":"","phase":"Disabled","message":"Auto optimization is disabled.","snapshotCount":0,"measurementCount":0,"trainingSamples":0,"bestRMSE":null,"bestR":null,"bestLoss":null,"validationSamples":0,"lastRunAt":null,"nextRunAt":null,"lastOutcome":null,"leaseHolder":null,"leaseExpiresAt":null},"anchored":["Anchor One"]}
        """;

    [Test]
    public void RestCalibration_SerializesToPinnedJson()
    {
        var json = JsonSerializer.Serialize(_f.CreateController().GetCalibration(), CalibrationFixture.RestJson);
        Assert.That(json, Is.EqualTo(ExpectedRestJson));
    }

    [Test]
    public void Calculate_LooksUpEachNodesSettingsOnce()
    {
        _f.CreateController().GetCalibration();

        // Three nodes in a full mesh plus an anchored device: 8 pairs, but only 3 distinct nodes.
        foreach (var id in new[] { "node-a", "node-b", "node-c" })
            _f.Nsd.Verify(x => x.Get(id), Moq.Times.Once(), id);
    }

    internal const string ExpectedCollisionJson = """
        {"rmse":0.411877235523957,"r":0.9225598220766899,"bestRMSE":0.411877235523957,"bestR":0.9225598220766899,"matrix":{"Alpha":{"Bravo":{"tx_ref_rssi":-59,"mapDistance":4,"distance":4.5,"rssi":-71,"diff":0.5,"percent":0.125,"var":0.25},"Charlie":{"tx_ref_rssi":-59,"rx_adj_rssi":-3,"mapDistance":3,"distance":2.5,"rssi":-66,"diff":-0.5,"percent":-0.16666666666666666}},"Bravo":{"Alpha":{"tx_ref_rssi":-61,"rx_adj_rssi":2,"absorption":2.7,"mapDistance":6,"distance":5.5,"rssi":-75,"diff":-0.5,"percent":-0.08333333333333333,"var":0.5},"Charlie":{"tx_ref_rssi":-61,"rx_adj_rssi":-3,"mapDistance":5,"distance":5.5,"rssi":-78,"diff":0.5,"percent":0.1,"var":"NaN"}},"Charlie":{"Alpha":{"rx_adj_rssi":2,"absorption":2.7,"mapDistance":3,"distance":3.25,"rssi":-64,"diff":0.25,"percent":0.08333333333333333,"var":0.125},"Bravo":{"mapDistance":5,"distance":4.75,"rssi":-77,"diff":-0.25,"percent":-0.05,"var":1}}},"optimizerState":{"optimizers":"","phase":"Disabled","message":"Auto optimization is disabled.","snapshotCount":0,"measurementCount":0,"trainingSamples":0,"bestRMSE":null,"bestR":null,"bestLoss":null,"validationSamples":0,"lastRunAt":null,"nextRunAt":null,"lastOutcome":null,"leaseHolder":null,"leaseExpiresAt":null},"anchored":["Bravo"]}
        """;

    /// <summary>An anchored device named like a node merges into that node's row and cell, as it always has.</summary>
    [Test]
    public void RestCalibration_NameCollision_SerializesToPinnedJson()
    {
        _f.UseAnchorNamedLikeNode();
        var json = JsonSerializer.Serialize(_f.CreateController().GetCalibration(), CalibrationFixture.RestJson);
        Assert.That(json, Is.EqualTo(ExpectedCollisionJson));
    }
}
