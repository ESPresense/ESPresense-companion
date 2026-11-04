namespace ESPresense.Models;

/// <summary>
/// Why a device's reported room changed, published as a diagnostic sensor. The fix itself is on the explain
/// page, but only for the moment you look: this keeps the last change, so a room flip can be read afterwards.
/// </summary>
public sealed record AreaSwitch(
    string From, string To, string Reason,
    string? Scenario, string? PreviousScenario,
    double? Probability, int? Confidence, int? Fixes,
    DateTime At)
{
    private const int MaxHaState = 255;

    public static string Topic(string deviceId) => $"espresense/companion/{deviceId}/area_switch";

    /// <param name="previous">The scenario reported before this change, null if there wasn't one</param>
    public static AreaSwitch Describe(Device device, Scenario? previous)
    {
        var best = device.BestScenario;
        var from = device.PreviousReportedState ?? "not_home";
        var to = device.ReportedState ?? "not_home";
        var reason = Explain(device, best, previous, from, to);

        return new AreaSwitch(
            from, to, reason.Length > MaxHaState ? reason[..MaxHaState] : reason,
            best?.Label, previous?.Label,
            best?.Probability, best?.Confidence, best?.Fixes,
            device.ReportedStateChanged ?? DateTime.UtcNow);
    }

    private static string Explain(Device device, Scenario? best, Scenario? previous, string from, string to)
    {
        if (device.IsAnchored) return to == "not_home" ? "Anchored, and no node hears it" : "Anchored";
        if (best == null) return "No current fix: nodes stopped hearing it";
        if (to == "not_home") return $"{best.Label} placed it outside every floor";
        if (from == "not_home") return $"{best.Label} located it again";
        if (previous == null) return $"{best.Label} located it";
        if (previous != best) return $"{best.Label} took over from {previous.Label}";
        return best.Room == null ? $"{best.Label} moved it outside every room" : $"{best.Label} moved it across a wall";
    }
}
