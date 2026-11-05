namespace ESPresense.Models;

public class ProposedValues
{
    public double? RxAdjRssi { get; set; }
    public double? TxRefRssi { get; set; }
    public double? Absorption { get; set; }
    public double? Error { get; set; }

    /// <summary>
    /// Trust: the variance (dB^2) of the node's leave-one-out residuals on located devices, from the
    /// device_residual optimizer. A noisy node is down-weighted (1 / (variance + 4)) rather than dropped:
    /// a biased node is a reliable node with a wrong zero, and dropping nodes makes fixes jump.
    /// </summary>
    public double? DeviceResidualVariance { get; set; }
}

/// <summary>A device's rssi@1m as learned by the device_residual optimizer, with what the decision to apply it rests on.</summary>
public class ProposedDeviceValues
{
    /// <summary>The learned rssi@1m (absolute dBm).</summary>
    public double RefRssi { get; set; }

    /// <summary>The rssi@1m the device was ranged with in its newest window.</summary>
    public double CurrentRefRssi { get; set; }

    /// <summary>Training windows the device has.</summary>
    public int Windows { get; set; }

    /// <summary>Spread of its solved positions over 1.5 m, clipped to [0.05, 1]: a device that sits still teaches little.</summary>
    public double Mobility { get; set; }

    /// <summary>Consecutive cycles in which the learned value moved by less than 1 dB.</summary>
    public int StableCycles { get; set; }
}