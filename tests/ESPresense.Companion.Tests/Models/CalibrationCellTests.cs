using System.Text.Json;
using System.Text.RegularExpressions;
using ESPresense.Companion.Tests.Controllers;
using ESPresense.Models;
using ESPresense.Utils;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ESPresense.Companion.Tests.Models;

public class CalibrationCellTests
{
    [Test]
    public void Serializes_AllKeysInWireOrder()
    {
        var cell = new CalibrationCell(-59, 2, 2.7, 4, 4.5, -71, 0.25);
        Assert.That(JsonSerializer.Serialize(cell, CalibrationFixture.RestJson), Is.EqualTo(
            """{"tx_ref_rssi":-59,"rx_adj_rssi":2,"absorption":2.7,"mapDistance":4,"distance":4.5,"rssi":-71,"diff":0.5,"percent":0.125,"var":0.25}"""));
    }

    [Test]
    public void Serializes_OmitsNullOptionals_AndZeroMapDistanceGivesZeroPercent()
    {
        var cell = new CalibrationCell(null, null, null, 0, 1.5, -80, null);
        Assert.That(JsonSerializer.Serialize(cell, CalibrationFixture.RestJson), Is.EqualTo(
            """{"mapDistance":0,"distance":1.5,"rssi":-80,"diff":1.5,"percent":0}"""));
    }

    [Test]
    public void Serializes_NonFiniteAsNamedLiterals()
    {
        var cell = new CalibrationCell(null, null, null, 1, 1, -60, double.NaN);
        Assert.That(JsonSerializer.Serialize(cell, CalibrationFixture.RestJson), Does.EndWith(""","var":"NaN"}"""));
    }

    [Test]
    public void RoundTrips()
    {
        var cell = new CalibrationCell(-59, null, 3.1, 4, 4.5, -71, null);
        var back = JsonSerializer.Deserialize<CalibrationCell>(JsonSerializer.Serialize(cell, CalibrationFixture.RestJson), CalibrationFixture.RestJson);
        Assert.That(back, Is.EqualTo(cell));
    }

    [Test]
    public void IndexerAndContainsKey_UseWireKeys()
    {
        var cell = new CalibrationCell(null, -10, null, 2, 3, -65, null);
        Assert.That(cell[CalibrationCellKeys.RxAdjRssi], Is.EqualTo(-10));
        Assert.That(cell[CalibrationCellKeys.Diff], Is.EqualTo(1));
        Assert.That(cell.ContainsKey(CalibrationCellKeys.TxRefRssi), Is.False);
        Assert.Throws<KeyNotFoundException>(() => _ = cell[CalibrationCellKeys.Var]);
    }

    [Test]
    public void MergeOver_KeepsOptionalValuesTheNewCellLacks()
    {
        var first = new CalibrationCell(-59, null, 2.7, 4, 4.5, -71, 0.25);
        var second = new CalibrationCell(null, 2, null, 2, 2.25, -60, null);
        Assert.That(second.MergeOver(first), Is.EqualTo(new CalibrationCell(-59, 2, 2.7, 2, 2.25, -60, 0.25)));
        Assert.That(second.MergeOver(null), Is.SameAs(second));
    }

    /// <summary>The UI's hand-written type must list exactly the keys C# emits, with the same optionality.</summary>
    [Test]
    public void UiType_MatchesCellKeys()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ui", "src", "lib", "types.ts"))) dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "src/ui/src/lib/types.ts not found above the test directory");
        var ts = File.ReadAllText(Path.Combine(dir!.FullName, "src", "ui", "src", "lib", "types.ts"));

        var cellBody = CellTypeBody(ts);
        var fields = Regex.Matches(cellBody, @"(?<key>\w+)(?<opt>\?)?\s*:")
            .Select(m => (Key: m.Groups["key"].Value, Optional: m.Groups["opt"].Success))
            .ToList();

        Assert.That(fields.Select(f => f.Key), Is.EqualTo(CalibrationCellKeys.All));
        Assert.That(fields.Where(f => f.Optional).Select(f => f.Key), Is.EqualTo(CalibrationCellKeys.Optional));
    }

    /// <summary>
    /// The body of NodeCalibrationMatrix's innermost <c>[rxName: string]: { ... }</c>, found by brace matching so a
    /// nested object or a brace in a comment cannot silently cut it short.
    /// </summary>
    private static string CellTypeBody(string ts)
    {
        const string iface = "export interface NodeCalibrationMatrix {";
        const string cell = "[rxName: string]: {";
        var i = ts.IndexOf(iface, StringComparison.Ordinal);
        Assert.That(i, Is.GreaterThanOrEqualTo(0), $"could not parse types.ts: '{iface}' not found");
        var start = ts.IndexOf(cell, i, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"could not parse types.ts: '{cell}' not found in NodeCalibrationMatrix");
        start += cell.Length;

        var depth = 1;
        for (var j = start; j < ts.Length; j++)
        {
            if (ts[j] == '{') depth++;
            else if (ts[j] == '}' && --depth == 0)
            {
                var body = ts[start..j];
                Assert.That(body, Does.Not.Contain("{"), "could not parse types.ts: the cell type now nests an object; update this test");
                return body;
            }
        }
        Assert.Fail("could not parse types.ts: unbalanced braces in NodeCalibrationMatrix");
        return "";
    }

    /// <summary>Swagger documents the cell under its wire keys, with only the omittable keys optional.</summary>
    [Test]
    public void SwaggerSchema_UsesWireKeys()
    {
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions { SchemaFilters = { new CalibrationCellSchemaFilter() } },
            new JsonSerializerDataContractResolver(JsonDefaults.Web));
        var repo = new SchemaRepository();
        generator.GenerateSchema(typeof(CalibrationCell), repo);

        var schema = repo.Schemas[nameof(CalibrationCell)];
        Assert.That(schema.Properties!.Keys, Is.EquivalentTo(CalibrationCellKeys.All));
        Assert.That(schema.Required, Is.EquivalentTo(CalibrationCellKeys.All.Except(CalibrationCellKeys.Optional)));
    }
}
