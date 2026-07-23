namespace ESPresense.Models;

public class OptimizerState
{
    public string Optimizers { get; set; } = string.Empty;
    public string Phase { get; set; } = "Disabled";
    public string Message { get; set; } = "Auto optimization is disabled.";
    public int SnapshotCount { get; set; }
    public int MeasurementCount { get; set; }
    public int TrainingSamples { get; set; }
    public double? BestRMSE { get; set; }
    public double? BestR { get; set; }
    public double? BestLoss { get; set; }
    public int ValidationSamples { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public string? LastOutcome { get; set; }
    public string? LeaseHolder { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
}
