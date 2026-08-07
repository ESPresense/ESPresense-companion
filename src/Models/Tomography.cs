namespace ESPresense.Models;

/// <summary>
/// A reconstructed static RF-attenuation field for one floor: a grid of "extra path loss beyond
/// free space" inferred from the node-to-node links that cross each cell. High-attenuation cells
/// are where the radio sees something solid (walls, appliances, a refrigerator).
/// </summary>
public class TomographyFloor
{
    public string? FloorId { get; set; }
    public string? FloorName { get; set; }

    // Grid origin (metres) and geometry.
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double CellSize { get; set; }
    public int Cols { get; set; }
    public int Rows { get; set; }

    /// <summary>Row-major (row * Cols + col) attenuation in dB per metre, clamped to >= 0.</summary>
    public double[] Attenuation { get; set; } = System.Array.Empty<double>();

    /// <summary>Row-major ray coverage per cell (total link length through the cell). Low = unreliable.</summary>
    public double[] Coverage { get; set; } = System.Array.Empty<double>();

    public int Links { get; set; }
    public double MaxAttenuation { get; set; }
}

public class TomographyResult
{
    public System.DateTime Updated { get; set; }
    public System.Collections.Generic.List<TomographyFloor> Floors { get; set; } = new();
}
