using ESPresense.Models;

namespace ESPresense.Optimizers;

/// <summary>What one cycle concluded: the report the debugging tools return, and the proposal to apply, if any.</summary>
internal sealed record OptimizationCycleResult(
    OptimizerCycleReport Report,
    OptimizerResidualsReport? Residuals,
    IOptimizer? Selected,
    OptimizationResults? SelectedResults,
    int BaselineSamples)
{
    /// <summary>The node settings the cycle froze when it started and scored every proposal against, by node id.</summary>
    public IReadOnlyDictionary<string, NodeSettings> Incumbent { get; init; } = new Dictionary<string, NodeSettings>();

    /// <summary>
    /// The result once the runner has tried to apply <see cref="Selected"/>: applied (all nodes written) or not, with
    /// the outcome to report and, when writing failed, why.
    /// </summary>
    public OptimizationCycleResult WithApplication(bool applied, string outcome, string? applyError = null) => this with
    {
        Report = Report with
        {
            Applied = applied,
            Outcome = outcome,
            ApplyError = applyError,
            Proposals = Report.Proposals.Select(p => p.Decision == OptimizerGateDecision.Selected ? p with { Applied = applied } : p).ToList()
        }
    };

    /// <summary>The result once the runner has written the learned rssi@1m of <paramref name="written"/> devices.</summary>
    public OptimizationCycleResult WithDevicesApplied(IReadOnlySet<string> written) => written.Count == 0 ? this : this with
    {
        Report = Report with
        {
            Outcome = $"{Report.Outcome} Wrote learned rssi@1m to {written.Count} device{(written.Count == 1 ? "" : "s")}.",
            Devices = Report.Devices?.Select(d => written.Contains(d.Device) ? d with { Applied = true } : d).ToList()
        }
    };
}

/// <summary>
/// The decision half of an optimization cycle: scores the incumbent and every optimizer's proposal and runs the gate
/// described on <see cref="OptimizationRunner.RunCycleAsync"/>, and decides which learned device rssi@1m values
/// would be written (<see cref="DecideDeviceReference"/>). It is given a function that reads node settings and
/// nothing else, so it cannot write settings or publish anything: the live runner applies what it selects, and a dry
/// run (<see cref="OptimizationRunner.DryRunAsync"/>) only reports it. The report it returns never says
/// <see cref="OptimizerCycleReport.Applied"/>: only the runner knows that, once the writes have succeeded
/// (<see cref="OptimizationCycleResult.WithApplication"/>).
/// </summary>
internal static class OptimizationCycle
{
    internal const string IncumbentScenario = "incumbent";

    internal static string DeviceFrameScenario(string optimizer) => $"{optimizer} (txRef held)";

    internal static double Composite(double corr, double rmse, ConfigOptimization optimization)
        => (corr * optimization.CorrelationWeight) + ((1 - rmse / (1 + rmse)) * optimization.RmseWeight);

    /// <summary>The device term of the composite: device_weight x (1 - e / (1 + e)), e the mean |leave-one-out residual| in dB.</summary>
    internal static double DeviceTerm(DeviceEvaluation device, ConfigOptimization optimization)
        => optimization.DeviceWeight * (1 - device.DeviceRmse / (1 + device.DeviceRmse));

    /// <summary>Learned rssi@1m outside this range is not written: no BLE device is that loud or that quiet at 1 m.</summary>
    internal const int DeviceRefMin = -100, DeviceRefMax = -20;

    /// <summary>A learned rssi@1m must have held within 1 dB for this many cycles before it is written.</summary>
    internal const int DeviceRefStableCycles = 2;

    /// <summary>
    /// A learned rssi@1m closer than this to the device's current one is not written. Node changes are centred, so
    /// every device's learned value carries the same sub-dB share of the nodes' mean; writing that to every device
    /// would be churn, not calibration.
    /// </summary>
    internal const double DeviceRefDeadbandDb = 1;

    /// <summary>
    /// Whether a device's learned rssi@1m is written to its device settings: only with apply_device_ref, for a
    /// device with min_windows windows that moves (mobility at least 0.3: a device that sits still cannot tell its
    /// own loudness from its spot's quirks), whose value has held within 1 dB for <see cref="DeviceRefStableCycles"/>
    /// cycles, that is not anchored (anchors are ground truth), and when the value is plausible and differs from the
    /// current one by at least <see cref="DeviceRefDeadbandDb"/>. Not part of the composite: every proposal is scored
    /// with the same learned rssi@1m values, whether or not they are written.
    /// </summary>
    internal static OptimizerDeviceReference DecideDeviceReference(string id, ProposedDeviceValues proposal, DeviceSettings? existing, ConfigDeviceResidual settings)
    {
        var value = (int)Math.Round(proposal.RefRssi);
        double current = existing?.RefRssi ?? proposal.CurrentRefRssi;
        var decision =
            !settings.ApplyDeviceRef ? OptimizerDeviceReferenceDecision.Disabled :
            proposal.Windows < settings.MinWindows ? OptimizerDeviceReferenceDecision.TooFewWindows :
            proposal.Mobility < DeviceResidualOptimizer.MovingMobility ? OptimizerDeviceReferenceDecision.SitsStill :
            proposal.StableCycles < DeviceRefStableCycles ? OptimizerDeviceReferenceDecision.NotYetStable :
            existing?.HasAnchor == true ? OptimizerDeviceReferenceDecision.Anchored :
            value < DeviceRefMin || value > DeviceRefMax ? OptimizerDeviceReferenceDecision.OutOfRange :
            Math.Abs(proposal.RefRssi - current) < DeviceRefDeadbandDb || value == (int)Math.Round(current) ? OptimizerDeviceReferenceDecision.Unchanged :
            OptimizerDeviceReferenceDecision.Apply;
        return new OptimizerDeviceReference(id, value, Math.Round(proposal.RefRssi, 1), current, proposal.Windows,
            Math.Round(proposal.Mobility, 2), proposal.StableCycles, decision, false);
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);

    // The gates below are written as !(score <= bar) rather than score > bar on purpose: that is how the gate has
    // always compared, and the two differ when the bar is NaN (a baseline without a defined correlation).

    /// <param name="os">The (pooled) snapshot the optimizers fit.</param>
    /// <param name="oss">The held-out snapshots proposals are scored on. <paramref name="os"/> itself is excluded unless it is all there is.</param>
    /// <param name="readSettings">Reads a node's current settings. Called once per node; the cycle scores against that frozen view.</param>
    /// <param name="readDeviceSettings">Reads a device's settings, to decide whether its learned rssi@1m would be written; null treats every device as having none.</param>
    public static OptimizationCycleResult Evaluate(
        OptimizationSnapshot os,
        IReadOnlyList<OptimizationSnapshot> oss,
        ConfigOptimization optimization,
        IEnumerable<IOptimizer> optimizers,
        Func<string, NodeSettings> readSettings,
        bool dryRun,
        DateTime at,
        Func<string, DeviceSettings?>? readDeviceSettings = null)
    {
        var evalSet = oss.Where(s => !ReferenceEquals(s, os)).ToList();
        if (evalSet.Count == 0) evalSet = oss.ToList();
        var settings = OptimizerGateSettings.From(optimization);

        // Freeze the incumbent settings once for the whole cycle. NodeSettingsStore.Get reads whatever the
        // store holds at that moment, so scoring straight from it would let the same proposal score
        // differently depending on whether an earlier cycle's MQTT writes had been echoed back yet.
        var frozen = new Dictionary<string, NodeSettings>(StringComparer.Ordinal);
        foreach (var id in os.GetNodeIds().Concat(evalSet.SelectMany(s => s.GetNodeIds())).Distinct())
            frozen[id] = readSettings(id);
        NodeSettings Frozen(string id) => frozen.TryGetValue(id, out var ns) ? ns : readSettings(id);

        // Each scenario is predicted once; the same predictions are scored and fed to the residual report.
        var residuals = new ResidualAccumulator();
        OptimizationEvaluation Score(string scenario, OptimizationResults results, bool deviceFrame = false)
        {
            var predictions = results.Predict(evalSet, Frozen, deviceFrame).ToList();
            residuals.Add(scenario, predictions);
            return OptimizationResults.Score(predictions);
        }

        // The device term (#1860) exists only in device_residual mode, with held-out device windows and a weight to
        // give them. Snapshots taken before a switch to another mode still carry windows, so the mode is checked, not
        // just the windows. Without it every composite below is exactly the node-to-node one.
        var scoreDevices = optimization.DeviceResidualMode && optimization.DeviceWeight > 0 && evalSet.Any(s => s.DeviceWindows.Count > 0);
        var minDeviceNodes = optimization.DeviceResidual.EffectiveMinNodes;
        var noDevices = (Total: DeviceEvaluation.None, PerSnapshot: Enumerable.Repeat(DeviceEvaluation.None, evalSet.Count).ToArray());
        Func<DeviceWindow, double>? deviceRef = null; // each device's learned rssi@1m, once the optimizers have fitted
        (DeviceEvaluation Total, DeviceEvaluation[] PerSnapshot) ScoreDevices(OptimizationResults results) =>
            scoreDevices ? results.EvaluateDevices(evalSet, Frozen, minDeviceNodes, deviceRef) : noDevices;
        double WithDevices(double composite, DeviceEvaluation device) =>
            scoreDevices && device.HasData ? composite + DeviceTerm(device, optimization) : composite;
        OptimizerDeviceScore? DeviceScore(DeviceEvaluation device) =>
            scoreDevices && device.HasData ? new OptimizerDeviceScore(device.DeviceRmse, device.Scale, device.Windows, DeviceTerm(device, optimization)) : null;

        var baselineResults = new OptimizationResults();
        var (bestCorr, bestRmse, baselineSamples) = Score(IncumbentScenario, baselineResults);
        var evalDeviceWindows = evalSet.SelectMany(s => s.DeviceWindows).ToList();

        if (baselineSamples < optimization.MinSamples)
        {
            var skipped = new OptimizerCycleReport(at, dryRun, os.Measures.Count, evalSet.Count, settings,
                new OptimizerScore(Composite(bestCorr, bestRmse, optimization), bestCorr, bestRmse, baselineSamples),
                Array.Empty<OptimizerProposalReport>(), null, false,
                $"Skipped: only {baselineSamples} validation measurements, need at least {optimization.MinSamples} (min_samples).")
            {
                DeviceWindows = os.DeviceWindows.Count + evalDeviceWindows.Count > 0 ? new OptimizerDeviceWindows(os.DeviceWindows.Count, evalDeviceWindows.Count, 0) : null
            };
            return new OptimizationCycleResult(skipped, null, null, null, baselineSamples) { Incumbent = frozen };
        }

        // Optimizers get their own copies so nothing they do can touch the frozen incumbent.
        var currentSettings = os.GetNodeIds().ToDictionary(id => id, id => Frozen(id).Clone());

        // Every optimizer fits before anything is scored: the device term needs the rssi@1m the device_residual
        // optimizer learned for each device.
        var fitted = optimizers.Select(o => (Optimizer: o, Results: o.Optimize(os, currentSettings).QuantizeForApplication())).ToList();
        var learned = new Dictionary<string, ProposedDeviceValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, results) in fitted)
            foreach (var (id, proposal) in results.Devices)
                learned[id] = proposal;

        // Held-out device windows are scored with each device's learned rssi@1m, the same for the baseline and every
        // proposal, rather than the one it was ranged with. The device term exists to judge node calibration. Ranged
        // with their own rssi@1m, windows mostly measure how wrong those are (live, a phone and a tag 7-15 dB off,
        // solver scale 1.38 held out against 1.01 in training), which no node proposal can fix (their rxAdj
        // changes are centred) and which stays wrong while learned values are not written (apply_device_ref off,
        // or a device that never passes its guards). A device the optimizers learned nothing for keeps its own.
        if (learned.Count > 0)
            deviceRef = w => learned.TryGetValue(w.DeviceId, out var p) ? p.RefRssi : w.RefRssi;

        var baselineDevices = ScoreDevices(baselineResults);
        var bestScore = WithDevices(Composite(bestCorr, bestRmse, optimization), baselineDevices.Total);
        var baseline = new OptimizerScore(bestScore, bestCorr, bestRmse, baselineSamples) { Device = DeviceScore(baselineDevices.Total) };
        var deviceWindows = os.DeviceWindows.Count + evalDeviceWindows.Count > 0
            ? new OptimizerDeviceWindows(os.DeviceWindows.Count, evalDeviceWindows.Count, evalDeviceWindows.Count(w => learned.ContainsKey(w.DeviceId)))
            : null;

        // Each eval snapshot's incumbent score, for the every-snapshot gate; the same for every proposal.
        var baselinePerSnapshot = evalSet.Count > 1
            ? evalSet.Select(snap => baselineResults.Evaluate(new List<OptimizationSnapshot> { snap }, Frozen)).ToList()
            : null;

        var scored = new List<(IOptimizer Optimizer, OptimizationResults Results, OptimizerScore Score, OptimizerGates Gates, OptimizerGateDecision Decision)>();
        foreach (var (optimizer, results) in fitted)
        {
            var (corr, rmse, samples) = Score(optimizer.Name, results);
            var devices = ScoreDevices(results);
            var composite = WithDevices(Composite(corr, rmse, optimization), devices.Total);
            var score = new OptimizerScore(composite, corr, rmse, samples) { Device = DeviceScore(devices.Total) };

            var minSamples = new OptimizerMinSamplesGate(samples, optimization.MinSamples, samples >= optimization.MinSamples);
            var improvement = new OptimizerImprovementGate(composite, bestScore, optimization.MinimumImprovement,
                IsFinite(composite) && !(composite <= bestScore + optimization.MinimumImprovement));

            // A proposal that moves txRef must also win with txRef held where it is: devices are ranged
            // with their own rssi@1m, so a txRef/rxAdj shift that cancels node-to-node is a plain range
            // bias for every device. See OptimizationResults.Evaluate.
            OptimizerDeviceFrameGate? deviceFrame = null;
            var movesTxRef = results.Nodes.Values.Any(r => r.TxRefRssi != null);
            if (movesTxRef)
            {
                var (dCorr, dRmse, _) = Score(DeviceFrameScenario(optimizer.Name), results, deviceFrame: true);
                var deviceComposite = WithDevices(Composite(dCorr, dRmse, optimization), devices.Total);
                deviceFrame = new OptimizerDeviceFrameGate(deviceComposite, dCorr, dRmse, bestScore,
                    IsFinite(deviceComposite) && !(deviceComposite <= bestScore));
            }

            // On a home that has not changed, successive snapshots differ only by noise, and rxAdj and
            // absorption trade off almost perfectly over the narrow band of distances one receiver sees.
            // A proposal that is ahead on the aggregate because one snapshot's noise favours it would be
            // applied, then undone by the next cycle's noise, and settings wander along that ridge
            // indefinitely. So it must also win against the incumbent on every eval snapshot by itself.
            OptimizerEverySnapshotGate? everySnapshot = null;
            if (baselinePerSnapshot != null)
            {
                var losesOn = evalSet.Where((snap, i) =>
                {
                    var (bc, br, bs) = baselinePerSnapshot[i];
                    var (pc, pr, ps) = results.Evaluate(new List<OptimizationSnapshot> { snap }, Frozen);
                    if (bs < optimization.MinSamples || ps < optimization.MinSamples) return false;
                    var propOne = WithDevices(Composite(pc, pr, optimization), devices.PerSnapshot[i]);
                    return double.IsNaN(propOne) || propOne <= WithDevices(Composite(bc, br, optimization), baselineDevices.PerSnapshot[i]);
                }).Count();
                everySnapshot = new OptimizerEverySnapshotGate(losesOn, evalSet.Count, losesOn == 0);
            }

            var decision =
                !minSamples.Passed ? OptimizerGateDecision.MinSamples :
                !improvement.Passed ? OptimizerGateDecision.NotBetter :
                deviceFrame is { Passed: false } ? OptimizerGateDecision.DeviceFrame :
                everySnapshot is { Passed: false } ? OptimizerGateDecision.EverySnapshot :
                OptimizerGateDecision.Passed;

            scored.Add((optimizer, results, score, new OptimizerGates(minSamples, improvement, deviceFrame, everySnapshot), decision));
        }

        // Every optimizer is scored against the same incumbent and only the best passing proposal is
        // applied. Applying them greedily in sequence made the outcome depend on list order, and let an
        // absorption-only proposal land on top of another optimizer's rxAdj: a pair nobody fitted.
        int? selected = null;
        for (var i = 0; i < scored.Count; i++)
            if (scored[i].Decision == OptimizerGateDecision.Passed && (selected == null || scored[i].Score.Composite > scored[selected.Value].Score.Composite))
                selected = i;

        var proposals = scored.Select((s, i) => new OptimizerProposalReport(
            s.Optimizer.Name,
            s.Score,
            i == selected ? OptimizerGateDecision.Selected : s.Decision,
            false,
            s.Gates,
            s.Results.Nodes.ToDictionary(kv => kv.Key, kv => OptimizerNodeProposal.From(kv.Value)))
        {
            DeviceResidual = s.Results.DeviceResidual
        }).ToList();

        // Learned device rssi@1m: decided on their own guards whichever node proposal (if any) is selected.
        var deviceReferences = learned.Count == 0 ? null : learned
            .Where(d => d.Value.Windows >= optimization.DeviceResidual.MinWindows)
            .OrderByDescending(d => d.Value.Windows).ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => DecideDeviceReference(d.Key, d.Value, readDeviceSettings?.Invoke(d.Key), optimization.DeviceResidual))
            .ToList();

        string outcome;
        IOptimizer? chosen = null;
        OptimizationResults? chosenResults = null;
        if (selected is { } sel)
        {
            var s = scored[sel];
            chosen = s.Optimizer;
            chosenResults = s.Results;
            outcome = $"{(dryRun ? "Would apply" : "Applying")} {s.Optimizer.Name} to {s.Results.Nodes.Count} nodes (held-out R={s.Score.R:0.000}, RMSE={s.Score.Rmse:0.000}).";
        }
        else
        {
            outcome = "No proposal beat the current calibration on the held-out data.";
        }

        var report = new OptimizerCycleReport(at, dryRun, os.Measures.Count, evalSet.Count, settings, baseline,
            proposals, chosen?.Name, false, outcome)
        {
            DeviceWindows = deviceWindows,
            Devices = deviceReferences,
        };
        return new OptimizationCycleResult(report, residuals.Build(at, dryRun, evalSet.Count), chosen, chosenResults, baselineSamples) { Incumbent = frozen };
    }

    /// <summary>Per node, per role, per scenario: count, sum and sum of squares of the residuals.</summary>
    private sealed class ResidualAccumulator
    {
        private readonly List<string> _scenarios = new();
        private readonly Dictionary<string, (string? Name, bool Anchor)> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Node, bool AsRx, string Scenario), (int N, double Sum, double SumSq)> _acc = new();

        public void Add(string scenario, IReadOnlyList<PredictedMeasure> predictions)
        {
            if (!_scenarios.Contains(scenario)) _scenarios.Add(scenario);
            foreach (var p in predictions)
            {
                var r = p.Residual;
                if (!IsFinite(r)) continue;
                Accumulate(p.Measure.Rx, true, scenario, r);
                Accumulate(p.Measure.Tx, false, scenario, r);
            }
        }

        private void Accumulate(OptNode node, bool asRx, string scenario, double r)
        {
            _nodes.TryAdd(node.Id, (node.Name, node.IsAnchor));
            var key = (node.Id, asRx, scenario);
            var (n, sum, sumSq) = _acc.GetValueOrDefault(key);
            _acc[key] = (n + 1, sum + r, sumSq + r * r);
        }

        private Dictionary<string, OptimizerResidualStats> Role(string id, bool asRx)
        {
            var role = new Dictionary<string, OptimizerResidualStats>(StringComparer.Ordinal);
            foreach (var scenario in _scenarios)
                if (_acc.TryGetValue((id, asRx, scenario), out var a) && a.N > 0)
                    role[scenario] = new OptimizerResidualStats(a.N, a.Sum / a.N, Math.Sqrt(a.SumSq / a.N));
            return role;
        }

        public OptimizerResidualsReport Build(DateTime at, bool dryRun, int evalSnapshots) => new(
            at, dryRun, evalSnapshots, _scenarios.ToList(),
            _nodes.OrderBy(n => n.Key, StringComparer.Ordinal)
                .Select(n => new OptimizerNodeResiduals(n.Key, n.Value.Name, n.Value.Anchor, Role(n.Key, true), Role(n.Key, false)))
                .ToList());
    }
}
