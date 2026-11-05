using System.Text.Json.Serialization;

namespace ESPresense.Models;

// Payloads of the optimizer debugging tools (#1857): get_optimizer_status, get_optimizer_last_cycle,
// get_optimizer_residuals, get_optimizer_measures and run_optimizer_dry_cycle, and their /api/optimizer twins.
// Serialized with JsonDefaults.Web like every other REST and MCP payload. RSSI and residuals are in dB,
// distances in metres, composites in the weighted score RunCycleAsync gates on (higher is better).

/// <summary>
/// What the optimizer is doing and what it has to work with. <see cref="Mode"/> is the mode that runs;
/// <see cref="ConfiguredMode"/> is what the config says, which differs when it names a removed or unknown mode and per_node_absorption runs instead.
/// </summary>
public sealed record OptimizerStatus(
    bool Enabled,
    string Mode,
    string ConfiguredMode,
    string Phase,
    string Message,
    IReadOnlyList<string> Optimizers,
    string? LeaseHolder,
    DateTime? LeaseExpiresAt,
    int SnapshotCount,
    int MeasurementCount,
    DateTime? OldestSnapshotAt,
    DateTime? NewestSnapshotAt,
    double? SpanMins,
    bool SufficientHistory,
    OptimizerSplitSizes? Split,
    DateTime? LastRunAt,
    DateTime? NextRunAt,
    string? LastOutcome,
    double? BestR,
    double? BestRmse,
    OptimizerGateSettings Settings);

/// <summary>How the current history would split: the older snapshots are pooled for training, the newest held out.</summary>
public sealed record OptimizerSplitSizes(int TrainingSnapshots, int TrainingMeasures, int ValidationSnapshots, int ValidationMeasures);

/// <summary>The configuration the gate applies (optimization: in config.yaml, with defaults filled in).</summary>
public sealed record OptimizerGateSettings(
    int MinSamples,
    double MinimumImprovement,
    double CorrelationWeight,
    double RmseWeight,
    double ValidationFraction,
    int TrainingWindowMins,
    int SampleIntervalSecs,
    int OptimizationIntervalSecs)
{
    public static OptimizerGateSettings From(ConfigOptimization o) => new(
        o.MinSamples, o.MinimumImprovement, o.CorrelationWeight, o.RmseWeight, o.EffectiveValidationFraction,
        o.EffectiveTrainingWindowMins, o.EffectiveSampleIntervalSecs, o.EffectiveOptimizationIntervalSecs);
}

/// <summary>Fit quality on the eval set: composite score, Pearson R and RMSE (dB) over <see cref="Samples"/> measures.</summary>
public sealed record OptimizerScore(double Composite, double R, double Rmse, int Samples);

/// <summary>
/// One optimization cycle: the incumbent's score on the eval set and every proposal with the gate's verdict.
/// <see cref="DryRun"/> cycles never apply anything; <see cref="Applied"/> is then always false and
/// <see cref="Selected"/> names what a live cycle would have applied. In a live cycle <see cref="Applied"/> becomes
/// true only once every node of the selected proposal has been written; while the writes are in progress the
/// outcome says "Applying", and if one fails <see cref="ApplyError"/> says why and <see cref="Applied"/> stays false.
/// </summary>
public sealed record OptimizerCycleReport(
    DateTime At,
    bool DryRun,
    int TrainingMeasures,
    int EvalSnapshots,
    OptimizerGateSettings Settings,
    OptimizerScore Baseline,
    IReadOnlyList<OptimizerProposalReport> Proposals,
    string? Selected,
    bool Applied,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ApplyError = null);

/// <summary>Where a proposal stopped. Gates are checked in this order; the first one failed is the decision.</summary>
public enum OptimizerGateDecision
{
    /// <summary>Passed every gate and scored best: applied (or, in a dry run, would be).</summary>
    Selected,
    /// <summary>Passed every gate but another passing proposal scored higher.</summary>
    Passed,
    /// <summary>Evaluated on fewer than min_samples measures.</summary>
    MinSamples,
    /// <summary>Composite not above the baseline plus minimum_improvement.</summary>
    NotBetter,
    /// <summary>Moves txRef and does not beat the baseline with txRef held.</summary>
    DeviceFrame,
    /// <summary>Better on the aggregate but not on every eval snapshot by itself.</summary>
    EverySnapshot,
}

public sealed record OptimizerProposalReport(
    string Optimizer,
    OptimizerScore Score,
    OptimizerGateDecision Decision,
    bool Applied,
    OptimizerGates Gates,
    IReadOnlyDictionary<string, OptimizerNodeProposal> Nodes);

/// <summary>
/// The numbers behind each gate. All are computed for every proposal, so a proposal stopped early still shows
/// how it would have fared later. <see cref="DeviceFrame"/> is null when the proposal leaves txRef alone, and
/// <see cref="EverySnapshot"/> when there is only one eval snapshot.
/// </summary>
public sealed record OptimizerGates(
    OptimizerMinSamplesGate MinSamples,
    OptimizerImprovementGate Improvement,
    OptimizerDeviceFrameGate? DeviceFrame,
    OptimizerEverySnapshotGate? EverySnapshot);

public sealed record OptimizerMinSamplesGate(int Samples, int Required, bool Passed);

/// <summary>Passed when <see cref="Composite"/> &gt; <see cref="Baseline"/> + <see cref="MinimumImprovement"/>.</summary>
public sealed record OptimizerImprovementGate(double Composite, double Baseline, double MinimumImprovement, bool Passed);

/// <summary>The proposal scored with every transmitter's txRef held at its current value. Passed when the composite beats the baseline.</summary>
public sealed record OptimizerDeviceFrameGate(double Composite, double R, double Rmse, double Baseline, bool Passed);

/// <summary>On how many of the eval snapshots, each scored by itself, the proposal did not beat the incumbent.</summary>
public sealed record OptimizerEverySnapshotGate(int LostOn, int Of, bool Passed);

/// <summary>A proposed value for one node, as it would be applied (absorption to 2 decimals, rxAdj and txRef to whole dB). Null means unchanged.</summary>
public sealed record OptimizerNodeProposal(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Absorption,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RxAdjRssi,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TxRefRssi,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Error)
{
    public static OptimizerNodeProposal From(ProposedValues v) => new(v.Absorption, v.RxAdjRssi, v.TxRefRssi, v.Error);
}

/// <summary>
/// Residuals (measured minus predicted RSSI, dB) on a cycle's eval set, per node and role, under the incumbent
/// settings and under each proposal. <see cref="Scenarios"/> lists the keys of each node's role dictionaries:
/// <c>incumbent</c>, each optimizer's name, and <c>"&lt;name&gt; (txRef held)"</c> for proposals that move txRef
/// (the frame the device-frame gate scores in).
/// </summary>
public sealed record OptimizerResidualsReport(
    DateTime At,
    bool DryRun,
    int EvalSnapshots,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<OptimizerNodeResiduals> Nodes);

/// <summary>A node's residuals as receiver and as transmitter, keyed by scenario. A role it never had is empty.</summary>
public sealed record OptimizerNodeResiduals(
    string Id,
    string? Name,
    bool Anchor,
    IReadOnlyDictionary<string, OptimizerResidualStats> AsRx,
    IReadOnlyDictionary<string, OptimizerResidualStats> AsTx);

public sealed record OptimizerResidualStats(int N, double Mean, double Rms);

/// <summary>
/// The pooled training snapshot the optimizers fit, one cell per transmitter/receiver pair. Unavailable (with a
/// reason) until the history is long enough to split.
/// </summary>
public sealed record OptimizerMeasuresReport(
    bool Available,
    string? Reason,
    DateTime? From,
    DateTime? To,
    int TrainingSnapshots,
    int Measures,
    IReadOnlyDictionary<string, string?> Names,
    IReadOnlyList<OptimizerMeasureCell> Cells);

/// <summary>
/// One tx/rx pair across the pooled training snapshots: mean RSSI as published (it includes the rxAdj the node had
/// at the time; <see cref="RxAdj"/> lists every value seen), its spread, mean refRssi, mean rssiVar, the map
/// distance and the mean measured distance. <see cref="TxAnchor"/> marks an anchored device as the transmitter.
/// </summary>
public sealed record OptimizerMeasureCell(
    string Tx,
    string Rx,
    bool TxAnchor,
    int Samples,
    double Rssi,
    double? RssiSd,
    IReadOnlyList<double?> RxAdj,
    double RefRssi,
    double? RssiVar,
    double MapDistance,
    double Distance);
