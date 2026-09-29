namespace ESPresense.Services.Occupancy;

/// <summary>
/// Coarse, device-free occupancy signal from WiFi CSI amplitude variance (esp-radar-style
/// presence detection — CMP-1, ESPA-197). Deliberately does not attempt pose/vitals/through-wall;
/// that is out of scope per the ESPA-168 feasibility doc. Variance thresholds are calibrated
/// against the synthetic ECF1 corpus (ESPA-173's ecf1.py amp_spread contract: vacant≈6, occupied≈18)
/// and must be re-tuned once FW-1 lands a measured corpus.
/// </summary>
public sealed class CsiOccupancyDetector
{
    private readonly TimeSpan _window;
    private readonly double _varianceFloor;
    private readonly double _varianceCeiling;
    private readonly Dictionary<string, Queue<(DateTimeOffset Ts, double[] Amplitudes)>> _buffers = new();

    public CsiOccupancyDetector(TimeSpan? window = null, double varianceFloor = 8.0, double varianceCeiling = 22.0)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
        _varianceFloor = varianceFloor;
        _varianceCeiling = varianceCeiling;
    }

    /// <summary>
    /// Feed one CSI frame's raw IQ payload (alternating int8 I/Q per subcarrier, per the
    /// firmware's binary CSI frame layout v1 / ECF1 REC_CSI payload) for a node, and get back
    /// that node's current rolling-window occupancy signal.
    /// </summary>
    public CsiOccupancySignal Observe(string nodeId, ReadOnlySpan<sbyte> iq, DateTimeOffset ts)
    {
        if (iq.Length % 2 != 0)
            throw new ArgumentException("IQ payload must contain an even number of I/Q bytes", nameof(iq));

        var amplitudes = new double[iq.Length / 2];
        for (var i = 0; i < amplitudes.Length; i++)
        {
            double re = iq[2 * i];
            double im = iq[2 * i + 1];
            amplitudes[i] = Math.Sqrt(re * re + im * im);
        }

        if (!_buffers.TryGetValue(nodeId, out var buffer))
        {
            buffer = new Queue<(DateTimeOffset, double[])>();
            _buffers[nodeId] = buffer;
        }

        buffer.Enqueue((ts, amplitudes));
        while (buffer.Count > 1 && ts - buffer.Peek().Ts > _window)
            buffer.Dequeue();

        var flattened = buffer.SelectMany(frame => frame.Amplitudes).ToArray();
        var variance = Variance(flattened);
        var confidence = Math.Clamp((variance - _varianceFloor) / (_varianceCeiling - _varianceFloor), 0.0, 1.0);

        return new CsiOccupancySignal(nodeId, ts, variance, confidence, buffer.Count);
    }

    private static double Variance(double[] values)
    {
        if (values.Length < 2) return 0.0;
        var mean = values.Average();
        var sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return sumSquares / (values.Length - 1);
    }
}

/// <summary>A node's rolling-window CSI amplitude-variance occupancy read.</summary>
public readonly record struct CsiOccupancySignal(string NodeId, DateTimeOffset Timestamp, double Variance, double Confidence, int WindowFrameCount);
