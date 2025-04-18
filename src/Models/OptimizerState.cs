namespace ESPresense.Models;

public class OptimizerState
{
    public string Optimizers { get; set; } = string.Empty;
    public double? BestRMSE { get; set; }
    public double? BestR { get; set; }
}
