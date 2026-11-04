using System.Text.Json;
using System.Text.Json.Serialization;

namespace ESPresense.Models;

/// <summary>
/// The JSON keys of a calibration matrix cell, defined once. The UI's <c>NodeCalibrationMatrix</c> type in
/// <c>src/ui/src/lib/types.ts</c> is checked against <see cref="All"/> by a unit test.
/// </summary>
public static class CalibrationCellKeys
{
    public const string TxRefRssi = "tx_ref_rssi";
    public const string RxAdjRssi = "rx_adj_rssi";
    public const string Absorption = "absorption";
    public const string MapDistance = "mapDistance";
    public const string Distance = "distance";
    public const string Rssi = "rssi";
    public const string Diff = "diff";
    public const string Percent = "percent";
    public const string Var = "var";

    /// <summary>Every key, in wire order.</summary>
    public static readonly IReadOnlyList<string> All = [TxRefRssi, RxAdjRssi, Absorption, MapDistance, Distance, Rssi, Diff, Percent, Var];

    /// <summary>Keys that are omitted when the value is unknown.</summary>
    public static readonly IReadOnlyList<string> Optional = [TxRefRssi, RxAdjRssi, Absorption, Var];
}

/// <summary>
/// One transmitter-to-receiver entry of the calibration matrix. Serialized as a flat object keyed by
/// <see cref="CalibrationCellKeys"/>; null values are left out.
/// </summary>
[JsonConverter(typeof(CalibrationCellJsonConverter))]
public sealed record CalibrationCell(
    double? TxRefRssi, double? RxAdjRssi, double? Absorption,
    double MapDistance, double Distance, double Rssi, double? Var)
{
    public double Diff => Distance - MapDistance;
    public double Percent => MapDistance != 0 ? Diff / MapDistance : 0;

    /// <summary>The cell's present values as (key, value) pairs, in wire order.</summary>
    public IEnumerable<KeyValuePair<string, double>> Entries()
    {
        if (TxRefRssi is { } tx) yield return new(CalibrationCellKeys.TxRefRssi, tx);
        if (RxAdjRssi is { } rx) yield return new(CalibrationCellKeys.RxAdjRssi, rx);
        if (Absorption is { } abs) yield return new(CalibrationCellKeys.Absorption, abs);
        yield return new(CalibrationCellKeys.MapDistance, MapDistance);
        yield return new(CalibrationCellKeys.Distance, Distance);
        yield return new(CalibrationCellKeys.Rssi, Rssi);
        yield return new(CalibrationCellKeys.Diff, Diff);
        yield return new(CalibrationCellKeys.Percent, Percent);
        if (Var is { } v) yield return new(CalibrationCellKeys.Var, v);
    }

    /// <summary>Whether the cell would emit <paramref name="key"/> on the wire.</summary>
    public bool ContainsKey(string key) => Entries().Any(e => e.Key == key);

    /// <summary>Reads a value by its wire key. Throws <see cref="KeyNotFoundException"/> if the cell omits it.</summary>
    public double this[string key] => Entries().FirstOrDefault(e => e.Key == key) is { Key: not null } e
        ? e.Value
        : throw new KeyNotFoundException(key);

    /// <summary>
    /// Combines this cell with one already stored under the same tx/rx names (a name collision). This cell's
    /// values win; optional values it lacks are kept from <paramref name="previous"/>, as the old
    /// per-key dictionary did.
    /// </summary>
    public CalibrationCell MergeOver(CalibrationCell? previous) => previous is null ? this : this with
    {
        TxRefRssi = TxRefRssi ?? previous.TxRefRssi,
        RxAdjRssi = RxAdjRssi ?? previous.RxAdjRssi,
        Absorption = Absorption ?? previous.Absorption,
        Var = Var ?? previous.Var
    };
}

/// <summary>Writes a <see cref="CalibrationCell"/> as the flat key/value object the UI and MCP clients read.</summary>
public sealed class CalibrationCellJsonConverter : JsonConverter<CalibrationCell>
{
    public override void Write(Utf8JsonWriter writer, CalibrationCell value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, v) in value.Entries())
        {
            writer.WritePropertyName(key);
            // Through the serializer so options.NumberHandling (NaN/Infinity as strings) applies as before.
            JsonSerializer.Serialize(writer, v, options);
        }
        writer.WriteEndObject();
    }

    public override CalibrationCell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, double>>(ref reader, options) ?? new();
        double? Opt(string k) => values.TryGetValue(k, out var v) ? v : null;
        return new CalibrationCell(
            Opt(CalibrationCellKeys.TxRefRssi), Opt(CalibrationCellKeys.RxAdjRssi), Opt(CalibrationCellKeys.Absorption),
            Opt(CalibrationCellKeys.MapDistance) ?? 0, Opt(CalibrationCellKeys.Distance) ?? 0, Opt(CalibrationCellKeys.Rssi) ?? 0,
            Opt(CalibrationCellKeys.Var));
    }
}
