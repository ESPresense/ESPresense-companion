namespace ESPresense.Services.Occupancy;

/// <summary>How strongly the fused signals agree on <see cref="RoomOccupancy.IsOccupied"/>.</summary>
public enum OccupancyConfidence
{
    Low,
    Medium,
    High
}

/// <summary>A fused per-room occupancy read (CMP-1, ESPA-197).</summary>
public sealed record RoomOccupancy(string RoomId, bool IsOccupied, OccupancyConfidence Confidence, string Reason);

/// <summary>
/// Fuses existing BLE device-based room presence with the new CSI device-free signal into one
/// room-occupancy read. This is a C# port of the validated truth table in FUSION_DESIGN.md /
/// occupancy_fusion.py's <c>fuse()</c> (PR #1708, ESPA-197) — deliberately a simple, inspectable
/// decision table rather than a learned model, because the named kill criteria (ESPA-195) are
/// about robustness and drift, not raw accuracy, and a one-line-explainable rule is one a
/// maintainer can debug against a real complaint thread.
///
/// Bias is toward NOT missing an occupant: either signal firing means "occupied". A false
/// "vacant" is the worse failure mode for this mission (HVAC-by-occupancy, elopement/fall
/// response), so a tracked BLE device never gets silently overridden by a quiet CSI reading, and
/// a device-free CSI hit is never dismissed just because no device is tracked.
/// </summary>
public sealed class OccupancyFusion
{
    private readonly double _csiOccupiedThreshold;

    public OccupancyFusion(double csiOccupiedThreshold = CsiOccupancyDetector.OccupiedThreshold)
    {
        _csiOccupiedThreshold = csiOccupiedThreshold;
    }

    /// <param name="blePresent">
    /// true/false/null. Null means no BLE-tracked device AND no reading taken — distinct from
    /// false, which means the companion's existing BLE pipeline actively checked and found
    /// nothing. Never ECF1's REC_BLE reference-advertiser record; that models QA-1's coexistence
    /// harness, not real per-room presence (see occupancy_fusion.py's scope-boundary note).
    /// </param>
    /// <param name="csiScore">
    /// The node's <see cref="CsiOccupancySignal.MotionScore"/>, or null if no CSI reading was
    /// available this window (node offline / no data this cycle).
    ///
    /// <c>Fuse</c> is stateless and does not itself check <see cref="CsiOccupancySignal.Timestamp"/>
    /// -- it trusts the caller to pass this cycle's score, not a cached stale one (flagged by
    /// CodeRabbit on PR #1707). That's a safe assumption today because the only caller
    /// (<c>RoomOccupancyPublisher</c>, CMP-2/ESPA-198) always passes null until FW-1 wires a real
    /// CSI feed. Whoever lands that wiring must enforce freshness (e.g. reject
    /// <see cref="CsiOccupancySignal"/> older than the detector's window) before extracting
    /// <see cref="CsiOccupancySignal.MotionScore"/> here -- do not assume it for free.
    /// </param>
    public RoomOccupancy Fuse(string roomId, bool? blePresent, double? csiScore)
    {
        var csiHit = csiScore is { } score && score >= _csiOccupiedThreshold;

        if (blePresent == true && csiHit)
            return new RoomOccupancy(roomId, true, OccupancyConfidence.High, "ble+csi agree occupied");

        if (blePresent == true)
            return new RoomOccupancy(roomId, true, OccupancyConfidence.Medium, "ble present, csi quiet/unavailable");

        if (blePresent == false && csiHit)
            return new RoomOccupancy(roomId, true, OccupancyConfidence.Medium, "no tracked device, csi indicates motion/energy");

        if (blePresent == false && csiScore is not null)
            return new RoomOccupancy(roomId, false, OccupancyConfidence.High, "ble+csi agree vacant");

        if (blePresent is null && csiHit)
            return new RoomOccupancy(roomId, true, OccupancyConfidence.Low, "csi indicates occupancy, ble signal unavailable");

        if (blePresent is null && csiScore is null)
            return new RoomOccupancy(roomId, false, OccupancyConfidence.Low, "no signal available -- treat as unknown/vacant with low confidence");

        return new RoomOccupancy(roomId, false, OccupancyConfidence.Medium, "ble absent, csi quiet");
    }
}
