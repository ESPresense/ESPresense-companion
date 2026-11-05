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

    /// <summary>The device_residual optimizer's last cycle; null when it has not run.</summary>
    public DeviceResidualState? DeviceResidual { get; set; }
}

/// <summary>What the device_residual optimizer learned from located devices in its last cycle.</summary>
public class DeviceResidualState
{
    public DateTime UpdatedAt { get; set; }

    /// <summary>Device windows trained on, and the devices they came from.</summary>
    public int Windows { get; set; }
    public int Devices { get; set; }

    /// <summary>Devices whose mobility is high enough for their rssi@1m to be applied.</summary>
    public int MovingDevices { get; set; }

    /// <summary>Mean |leave-one-out residual| (dB) over the training windows with the incumbent calibration and with the proposal.</summary>
    public double? ResidualBefore { get; set; }
    public double? ResidualAfter { get; set; }

    /// <summary>Mean solver scale (fitted / measured distance) before and after; a right calibration has scale near 1.</summary>
    public double? ScaleBefore { get; set; }
    public double? ScaleAfter { get; set; }

    /// <summary>Device rssi@1m values written to device settings by the last cycle.</summary>
    public int DevicesApplied { get; set; }

    /// <summary>Per node: trust (residual variance) and the rxAdj change proposed.</summary>
    public Dictionary<string, DeviceResidualNodeState> Nodes { get; set; } = new();
}

public class DeviceResidualNodeState
{
    /// <summary>Proposed rxAdj change this cycle (dB, after the per-cycle cap); null when the node had too little data to move.</summary>
    public double? RxAdjChange { get; set; }

    /// <summary>Variance (dB^2) of the node's leave-one-out residuals on devices.</summary>
    public double Variance { get; set; }

    /// <summary>Solver weight, 1 / (variance + 4).</summary>
    public double Weight { get; set; }

    /// <summary>Device windows the node was heard in.</summary>
    public int Windows { get; set; }
}
