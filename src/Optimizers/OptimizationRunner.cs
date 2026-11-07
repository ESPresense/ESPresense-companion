using ESPresense.Models;
using ESPresense.Services;
using ESPresense.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ESPresense.Optimizers;

internal class OptimizationRunner : BackgroundService
{
    private const string OptimizationLeaseName = "optimization";
    private readonly State _state;
    private readonly NodeSettingsStore _nsd;
    private readonly ILogger<OptimizationRunner> _logger;
    private readonly ConfigLoader _cfg;
    private readonly ILeaseService _leaseService;
    private readonly ILoggerFactory _loggerFactory;
    private IList<IOptimizer> _optimizers;
    private readonly object _optimizersLock = new();
    private string? _lastOptimizerMode;
    private double _previousBestCorr = double.NaN;
    private double _previousBestRmse = double.NaN;

    /// <summary>The measurement history the optimizers train and validate on.</summary>
    internal OptimizationSnapshotStore Snapshots { get; } = new();

    public OptimizationRunner(State state, NodeSettingsStore nsd, ILogger<OptimizationRunner> logger, ConfigLoader cfg, ILeaseService leaseService, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _state = state;
        _nsd = nsd;
        _logger = logger;
        _cfg = cfg;
        _leaseService = leaseService;
        _optimizers = new List<IOptimizer>();

        _cfg.ConfigChanged += (_, config) =>
        {
            var optimizerMode = NormalizeMode(config?.Optimization?.Optimizer);
            lock (_optimizersLock)
            {
                if (_lastOptimizerMode == optimizerMode)
                    return;
                _optimizers = BuildOptimizers(optimizerMode);
                _lastOptimizerMode = optimizerMode;
                _logger.LogInformation("Optimizer mode changed to {Mode}", optimizerMode);
            }
        };

        var initialMode = NormalizeMode(_cfg.Config?.Optimization?.Optimizer);
        _optimizers = BuildOptimizers(initialMode);
        _lastOptimizerMode = initialMode;
    }

    /// <summary>The only optimizer mode, and what an unset <c>optimization.optimizer</c> means.</summary>
    internal const string DefaultMode = "per_node_absorption";

    /// <summary>
    /// Modes that used to exist. Each was weaker by construction than the per-node joint fit:
    /// <c>legacy</c> chained four per-receiver optimizers that ignored transmitter effects, and
    /// <c>global_absorption</c> fitted one absorption for every receiver.
    /// </summary>
    private static readonly HashSet<string> RemovedModes = new(StringComparer.Ordinal) { "legacy", "global_absorption" };

    private static string NormalizeMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ? DefaultMode : mode.Trim().ToLowerInvariant();

    /// <summary>The optimizers the next cycle will run.</summary>
    internal IReadOnlyList<IOptimizer> CurrentOptimizers
    {
        get { lock (_optimizersLock) return _optimizers.ToList(); }
    }

    /// <summary>Applies the configured mode: always <see cref="DefaultMode"/>, with a warning when the config names anything else.</summary>
    private IList<IOptimizer> BuildOptimizers(string mode)
    {
        if (RemovedModes.Contains(mode))
            _logger.LogWarning("Optimizer mode {Mode} has been removed; using {DefaultMode}. Remove optimization.optimizer from the config (or set it to per_node_absorption) to silence this warning", mode, DefaultMode);
        else if (mode != DefaultMode)
            _logger.LogWarning("Unknown optimizer mode {Mode}; using {DefaultMode}", mode, DefaultMode);

        return CreateOptimizers();
    }

    /// <summary>Fresh optimizer instances for the one mode, without the mode warnings (dry runs build these too).</summary>
    private IList<IOptimizer> CreateOptimizers() =>
        new List<IOptimizer> { new PerNodeAbsorptionRxTx(_state, _loggerFactory.CreateLogger<PerNodeAbsorptionRxTx>()) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var optimization = _state.Config?.Optimization;
                while (optimization is not { Enabled: true })
                {
                    _logger.LogInformation("Optimization disabled");
                    SetStatus("Disabled", "Auto optimization is disabled.", optimizers: string.Empty);
                    _state.OptimizerState.NextRunAt = null;
                    _state.OptimizerState.LeaseHolder = null;
                    _state.OptimizerState.LeaseExpiresAt = null;
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    optimization = _state.Config?.Optimization;
                }

                // Try to acquire the optimization lease (wait indefinitely)
                SetStatus("WaitingForLease", "Waiting for MQTT connection or optimization lease availability.", optimizers: "Acquiring lease...");
                var leaseTask = _leaseService.AcquireAsync(
                    OptimizationLeaseName,
                    timeout: null, // Wait indefinitely
                    cancellationToken: stoppingToken);

                while (!leaseTask.IsCompleted)
                {
                    UpdateLeaseStatus();
                    await Task.WhenAny(leaseTask, Task.Delay(TimeSpan.FromSeconds(1), stoppingToken));
                }

                await using var lease = await leaseTask
                    ?? throw new InvalidOperationException("Optimization lease acquisition returned null unexpectedly.");

                _logger.LogInformation("Optimization enabled");
                SetStatus("Collecting", "Collecting snapshots for the initial training and validation split.", optimizers: GetOptimizerNames());
                _state.OptimizerState.LeaseHolder = "This instance";
                _state.OptimizerState.LeaseExpiresAt = _leaseService.GetStatus(OptimizationLeaseName)?.ExpiresAt;
                _previousBestCorr = double.NaN;
                _previousBestRmse = double.NaN;
                DateTime? lastOptimizationAt = null;

                // Sampling and optimizing run on separate clocks: snapshots are taken every sample interval,
                // while proposals are only evaluated once per optimization interval on the accumulated history.
                while (lease.HasLease())
                {
                    optimization = _state.Config?.Optimization;
                    if (optimization is not { Enabled: true }) break;
                    Snapshots.Take(_state);
                    UpdateSampleCounts();

                    var now = DateTime.UtcNow;
                    var optimizationDue = lastOptimizationAt == null ||
                                          now - lastOptimizationAt.Value >= TimeSpan.FromSeconds(optimization.EffectiveOptimizationIntervalSecs);
                    if (optimizationDue)
                    {
                        IList<IOptimizer> currentOptimizers;
                        lock (_optimizersLock)
                            currentOptimizers = _optimizers.ToList();

                        if (await RunScheduledCycleAsync(optimization, currentOptimizers))
                        {
                            lastOptimizationAt = now;
                            _state.OptimizerState.NextRunAt = now.AddSeconds(optimization.EffectiveOptimizationIntervalSecs);
                            SetStatus("Waiting", _state.OptimizerState.LastOutcome ?? "Validation cycle completed.");
                        }
                    }
                    else
                    {
                        SetStatus("Waiting", $"Collecting fresh samples; next validation at {_state.OptimizerState.NextRunAt:O}.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(optimization.EffectiveSampleIntervalSecs), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                SetStatus("Error", ex.Message, optimizers: "Error: " + ex.Message);
                _state.OptimizerState.NextRunAt = null;
                _logger.LogError(ex, "Error in OptimizationRunner");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>Share of the training window the history must span before the first proposal is evaluated.</summary>
    internal const double MinimumHistoryFraction = 0.5;

    /// <summary>
    /// A few seconds of history is enough to split but not enough to trust: a proposal fitted to two snapshots and
    /// checked on one would pass the held-out and every-snapshot checks almost vacuously, and would be pushed to
    /// real nodes within minutes of start-up. Wait until the history covers half the training window.
    /// </summary>
    internal static bool HasSufficientHistory(IReadOnlyCollection<OptimizationSnapshot> snapshots, ConfigOptimization optimization)
    {
        if (snapshots.Count < OptimizationDataSplit.MinimumSnapshots) return false;
        var span = snapshots.Max(s => s.Timestamp) - snapshots.Min(s => s.Timestamp);
        return span >= TimeSpan.FromMinutes(optimization.EffectiveTrainingWindowMins * MinimumHistoryFraction);
    }

    /// <summary>
    /// Splits the snapshot history by time: the older part is pooled into one training snapshot that the
    /// optimizers fit, and the newest <see cref="ConfigOptimization.ValidationFraction"/> is held out so
    /// <see cref="RunCycleAsync"/> only scores proposals on data they were not fitted to.
    /// Returns false (and keeps collecting) when there is not yet enough history for a split.
    /// </summary>
    internal async Task<bool> RunScheduledCycleAsync(ConfigOptimization optimization, IList<IOptimizer> currentOptimizers)
    {
        var snapshots = Snapshots.ToArray();
        if (!HasSufficientHistory(snapshots, optimization))
        {
            SetStatus("Collecting", $"Collected {snapshots.Length} snapshots; waiting until they span {MinimumHistoryFraction:P0} of the {optimization.EffectiveTrainingWindowMins}-minute training window before the first validation.");
            return false;
        }

        if (!OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var split) || split == null)
        {
            SetStatus("Collecting", $"Collected {snapshots.Length} of {OptimizationDataSplit.MinimumSnapshots} required snapshots.");
            _logger.LogInformation("Optimization deferred: only {SnapshotCount} snapshots are available", snapshots.Length);
            return false;
        }

        _state.OptimizerState.Optimizers = string.Join(", ", currentOptimizers.Select(o => o.Name));
        _state.OptimizerState.TrainingSamples = split.Training.Measures.Count;
        SetStatus("Optimizing", $"Training on {split.Training.Measures.Count} measurements and validating newer holdout data.");

        await RunCycleAsync(split.Training, split.Validation, optimization, currentOptimizers);
        _state.OptimizerState.LastRunAt = DateTime.UtcNow;
        return true;
    }

    private void SetStatus(string phase, string message, string? optimizers = null)
    {
        _state.OptimizerState.Phase = phase;
        _state.OptimizerState.Message = message;
        if (optimizers != null) _state.OptimizerState.Optimizers = optimizers;
    }

    private string GetOptimizerNames()
    {
        lock (_optimizersLock)
            return string.Join(", ", _optimizers.Select(optimizer => optimizer.Name));
    }

    private void UpdateSampleCounts()
    {
        _state.OptimizerState.SnapshotCount = Snapshots.Count;
        _state.OptimizerState.MeasurementCount = Snapshots.MeasurementCount;
        _state.OptimizerState.LeaseExpiresAt = _leaseService.GetStatus(OptimizationLeaseName)?.ExpiresAt;
    }

    private void UpdateLeaseStatus()
    {
        var status = _leaseService.GetStatus(OptimizationLeaseName);
        if (status == null || status.InstanceId == "nobody" || status.ExpiresAt <= DateTime.UtcNow)
        {
            _state.OptimizerState.LeaseHolder = null;
            _state.OptimizerState.LeaseExpiresAt = null;
            _state.OptimizerState.Message = "Waiting for MQTT connection or lease confirmation.";
            return;
        }

        _state.OptimizerState.LeaseHolder = status.IsOwned ? "This instance" : status.InstanceId;
        _state.OptimizerState.LeaseExpiresAt = status.ExpiresAt;
        _state.OptimizerState.Message = status.IsOwned
            ? "Confirming this instance's optimization lease."
            : $"Lease held by {status.InstanceId} until {status.ExpiresAt:O}.";
    }

    /// <summary>
    /// Runs one optimization pass: evaluates the incumbent settings over <paramref name="oss"/>, then each
    /// optimizer's proposal for <paramref name="os"/>, and applies the single best proposal that passes
    /// every check below. The checks exist because a calibration that is wrong but scores well is pushed
    /// to real nodes over MQTT, and the history of this gate is a history of proposals that did exactly that.
    ///
    /// Min samples: an evaluation backed by fewer than <see cref="ConfigOptimization.MinSamples"/>
    /// measurements is rejected outright; with a handful of points a proposal can fit the very samples
    /// it was derived from (RMSE ~ 0) and "beat" the baseline without having been validated by anything.
    ///
    /// Held-out scoring: when the history holds snapshots besides the one the optimizers fitted, proposals
    /// are scored only on those others. A proposal that memorised its own snapshot's radio noise scores
    /// well on itself however wrong it is, and on a cold start the gate would wave it straight through.
    ///
    /// Device frame: a proposal that moves txRef must also win with txRef held, because node-to-node data
    /// cannot tell "every transmitter k dB loud" from "every receiver k dB deaf" while every device, ranged
    /// with its own rssi@1m, can. Scoring in RSSI space is blind to that shift; the robot survey showed it
    /// as rxAdj climbing cycle after cycle.
    ///
    /// Every snapshot: the proposal must beat the incumbent on each eval snapshot on its own, not just on
    /// the aggregate. rxAdj and absorption trade off almost perfectly over the narrow band of distances
    /// one receiver sees, so on a home that has not changed, noise alone used to move settings by several
    /// dB every cycle along that ridge.
    ///
    /// Best of: proposals are all scored against the same incumbent and only the best is applied, rather
    /// than applied greedily in list order, which let an absorption-only proposal land on top of another
    /// optimizer's rxAdj: a pair nobody fitted.
    /// </summary>
    internal async Task RunCycleAsync(OptimizationSnapshot os, IReadOnlyList<OptimizationSnapshot> oss, ConfigOptimization optimization, IList<IOptimizer> optimizers)
    {
        // The decision is made without access to the store's writers; only the selected proposal is applied below.
        var cycle = OptimizationCycle.Evaluate(os, oss, optimization, optimizers, _nsd.Get, dryRun: false, DateTime.UtcNow);
        LastCycle = cycle;
        var report = cycle.Report;

        _state.OptimizerState.ValidationSamples = cycle.BaselineSamples;
        _state.OptimizerState.LastOutcome = report.Outcome;
        if (cycle.BaselineSamples < optimization.MinSamples)
        {
            _logger.LogDebug("Optimization skipped: baseline evaluated on {Samples} measurements, need at least {MinSamples} (min_samples)", cycle.BaselineSamples, optimization.MinSamples);
            return;
        }

        var (bestScore, bestCorr, bestRmse, _) = report.Baseline;
        if (double.IsNaN(_previousBestCorr) || double.IsNaN(_previousBestRmse) || Math.Abs(bestCorr - _previousBestCorr) > 0.001 || Math.Abs(bestRmse - _previousBestRmse) > 0.001)
        {
            _logger.LogInformation("Baseline metrics: Composite={Composite:0.000} (R={R:0.000}, RMSE={RMSE:0.000})", bestScore, bestCorr, bestRmse);
            _previousBestCorr = bestCorr;
            _previousBestRmse = bestRmse;
        }

        _state.OptimizerState.BestR = bestCorr;
        _state.OptimizerState.BestRMSE = bestRmse;

        foreach (var p in report.Proposals)
            LogDecision(p, bestScore, cycle.Incumbent);

        if (cycle.Selected == null || cycle.SelectedResults == null)
            return;

        var chosen = cycle.Selected;
        var chosenResults = cycle.SelectedResults;
        var chosenScore = report.Proposals.First(p => p.Decision == OptimizerGateDecision.Selected).Score;
        _logger.LogInformation("Applying {Optimizer} (Composite={Composite:0.000}, R={R:0.000}, RMSE={RMSE:0.000})", chosen.Name, chosenScore.Composite, chosenScore.R, chosenScore.Rmse);
        _state.OptimizerState.BestR = chosenScore.R;
        _state.OptimizerState.BestRMSE = chosenScore.Rmse;

        // The report says applied only once every node has been written: a publish that throws part way (MQTT down)
        // leaves it unapplied, with what was written and why it stopped, before the error reaches ExecuteAsync.
        var written = 0;
        try
        {
            await ApplyAsync(chosenResults, optimization, () => written++);
        }
        catch (Exception ex)
        {
            var failed = cycle.WithApplication(false,
                $"Failed applying {chosen.Name}: {written} of {chosenResults.Nodes.Count} nodes written before the error.", ex.Message);
            LastCycle = failed;
            _state.OptimizerState.LastOutcome = failed.Report.Outcome;
            throw;
        }

        var applied = cycle.WithApplication(true,
            $"Applied {chosen.Name} to {chosenResults.Nodes.Count} nodes (held-out R={chosenScore.R:0.000}, RMSE={chosenScore.Rmse:0.000}).");
        LastCycle = applied;
        _state.OptimizerState.LastOutcome = applied.Report.Outcome;
    }

    private async Task ApplyAsync(OptimizationResults chosenResults, ConfigOptimization optimization, Action nodeWritten)
    {
        foreach (var (id, result) in chosenResults.Nodes)
        {
            _logger.LogInformation("Applied {NodeId,-20}: Absorption={Absorption:0.00}, RxAdj={RxAdj:00}, TxAdj={TxAdj:00}, Error={Error}",
                id, result.Absorption, result.RxAdjRssi, result.TxRefRssi, result.Error);

            var nodeSettings = _nsd.Get(id);
            if (result.Absorption != null) nodeSettings.Calibration.Absorption = result.Absorption;
            if (result.RxAdjRssi != null) nodeSettings.Calibration.RxAdjRssi = (int?)Math.Round(result.RxAdjRssi.Value);
            if (result.TxRefRssi != null) nodeSettings.Calibration.TxRefRssi = (int?)Math.Round(result.TxRefRssi.Value);

            var atLimit = LimitsReached(
                result.Absorption != null ? nodeSettings.Calibration.Absorption : null,
                result.RxAdjRssi != null ? nodeSettings.Calibration.RxAdjRssi : null,
                result.TxRefRssi != null ? nodeSettings.Calibration.TxRefRssi : null,
                optimization);
            if (atLimit.Count > 0)
                _logger.LogWarning("Applied {NodeId,-20}: at the configured limit: {Limits}. The fit wanted to go further, so these are the limits rather than fitted values; check the node's placement or the optimization limits",
                    id, string.Join(", ", atLimit));

            await _nsd.Set(id, nodeSettings);
            nodeWritten();
        }
    }

    private void LogDecision(OptimizerProposalReport p, double bestScore, IReadOnlyDictionary<string, NodeSettings> incumbent)
    {
        var (composite, corr, rmse, samples) = p.Score;
        switch (p.Decision)
        {
            case OptimizerGateDecision.MinSamples:
                _logger.LogDebug("Optimizer {Optimizer,-24} rejected: evaluated on {Samples} measurements, need at least {MinSamples} (min_samples)",
                    p.Optimizer, samples, p.Gates.MinSamples.Required);
                break;
            case OptimizerGateDecision.NotBetter:
                _logger.LogInformation("Optimizer {Optimizer,-24} found worse results: Composite={Composite:0.000} <= Best={Best:0.000} (R={R:0.000}, RMSE={RMSE:0.000})",
                    p.Optimizer, composite, bestScore, corr, rmse);
                break;
            case OptimizerGateDecision.DeviceFrame:
                _logger.LogInformation("Optimizer {Optimizer,-24} rejected: better node-to-node (Composite={Composite:0.000}) but not with txRef held (Composite={DeviceComposite:0.000} <= Best={Best:0.000}); the gain is a txRef/rxAdj shift devices would see as range bias",
                    p.Optimizer, composite, p.Gates.DeviceFrame!.Composite, bestScore);
                break;
            case OptimizerGateDecision.EverySnapshot:
                _logger.LogInformation("Optimizer {Optimizer,-24} rejected: better on the aggregate (Composite={Composite:0.000} > Best={Best:0.000}) but not on {LosesOn} of {Snapshots} snapshots on their own; treating the gain as noise",
                    p.Optimizer, composite, bestScore, p.Gates.EverySnapshot!.LostOn, p.Gates.EverySnapshot.Of);
                break;
            case OptimizerGateDecision.NotEnoughSpots:
                _logger.LogInformation("Optimizer {Optimizer,-24} held: judging on spots needs {Required} devices at known positions, there are {Spots}",
                    p.Optimizer, p.Gates.Spots!.RequiredSpots, p.Gates.Spots.Spots);
                break;
            case OptimizerGateDecision.SpotsNotBetter:
            case OptimizerGateDecision.LinksWorse:
            case OptimizerGateDecision.GaugeShift:
                _logger.LogInformation("Optimizer {Optimizer,-24} rejected ({Decision}): better on {Better} and worse on {Worse} of {Spots} spots (z {SignZ:0.0}), spot RMS gain {GainDb:0.00} dB, node-link RMSE {LinkLossDb:+0.00;-0.00} dB, mean rxAdj shift {GaugeShiftDb:+0.0;-0.0} dB",
                    p.Optimizer, p.Decision, p.Gates.Spots!.Better, p.Gates.Spots.Worse, p.Gates.Spots.Spots, p.Gates.Spots.SignZ, p.Gates.Spots.GainDb, p.Gates.Spots.LinkLossDb, p.Gates.Spots.GaugeShiftDb);
                break;
            default:
                _logger.LogInformation("Optimizer {Optimizer,-24} found better results: Composite={Composite:0.000} > Best={Best:0.000} (R={R:0.000}, RMSE={RMSE:0.000})",
                    p.Optimizer, composite, bestScore, corr, rmse);
                break;
        }

        if (p.Decision is not (OptimizerGateDecision.Selected or OptimizerGateDecision.Passed))
            LogRejectedProposal(p, incumbent);
    }

    /// <summary>The most recent live cycle (not dry runs): its report, residuals and selection. Null until one has run.</summary>
    internal OptimizationCycleResult? LastCycle
    {
        get => Volatile.Read(ref _lastCycle);
        private set => Volatile.Write(ref _lastCycle, value);
    }
    private OptimizationCycleResult? _lastCycle;

    /// <summary>The optimizers the next scheduled cycle will run, by name.</summary>
    internal IReadOnlyList<string> OptimizerNames
    {
        get { lock (_optimizersLock) return _optimizers.Select(o => o.Name).ToList(); }
    }

    /// <summary>The optimizer mode that runs. per_node_absorption is the only one, whatever the config says.</summary>
    internal string Mode => DefaultMode;

    /// <summary>
    /// The optimizer mode as configured, lower-cased; it differs from <see cref="Mode"/> when the config still names a
    /// removed mode (legacy, global_absorption) or an unknown one.
    /// </summary>
    internal string ConfiguredMode
    {
        get { lock (_optimizersLock) return _lastOptimizerMode ?? DefaultMode; }
    }

    /// <summary>Whether this instance holds the optimization lease right now; snapshots are only taken while it does.</summary>
    internal bool HoldsLease => _leaseService.GetStatus(OptimizationLeaseName)?.IsOwned == true;

    /// <summary>
    /// Why the history may be empty or short, for messages: optimization is disabled (nothing is sampled), or this
    /// instance does not hold the lease (another instance samples). Null when neither applies.
    /// </summary>
    internal string? WhyNoSampling()
    {
        if (_state.Config?.Optimization is not { Enabled: true })
            return "Optimization is disabled (optimization.enabled), so no snapshots are collected.";
        if (!HoldsLease)
            return "Snapshots are only collected while this instance holds the optimization lease, and it does not.";
        return null;
    }

    /// <summary>Every optimizer a dry run can be asked for by name.</summary>
    internal IReadOnlyList<string> KnownOptimizerNames => CreateOptimizers().Select(o => o.Name).ToList();

    private readonly SemaphoreSlim _dryRunGate = new(1, 1);

    /// <summary>
    /// Runs one cycle now on the current history and reports it, applying nothing: the cycle is
    /// <see cref="OptimizationCycle.Evaluate"/>, which only ever reads node settings. It uses fresh optimizer instances, so
    /// no optimizer object is shared with a live cycle that may be running, does not touch <see cref="OptimizerState"/> or
    /// <see cref="LastCycle"/>, and needs no lease. Dry runs are serialized with each other but not with the live cycle:
    /// both read the same node settings, so a dry run issued while a live cycle is applying its proposal may score
    /// against a settings set that is only partly applied. The history is only collected while this instance holds
    /// the optimization lease, so on an instance that does not, there is nothing to run on.
    /// </summary>
    /// <param name="optimizer">Run only the optimizer with this name (case-insensitive), or the configured set when null.</param>
    internal async Task<OptimizationCycleResult> DryRunAsync(string? optimizer, CancellationToken cancellationToken = default)
    {
        var optimization = _state.Config?.Optimization ?? new ConfigOptimization();

        IList<IOptimizer> optimizers;
        if (string.IsNullOrWhiteSpace(optimizer))
        {
            optimizers = CreateOptimizers();
        }
        else
        {
            var match = CreateOptimizers().FirstOrDefault(o => string.Equals(o.Name, optimizer.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new UnknownOptimizerException($"Unknown optimizer '{optimizer}'. Known: {string.Join(", ", KnownOptimizerNames)}.");
            optimizers = new List<IOptimizer> { match };
        }

        var snapshots = Snapshots.ToArray();
        if (!OptimizationDataSplit.TryCreate(snapshots, optimization.EffectiveValidationFraction, out var split) || split == null)
            throw new OptimizerUnavailableException(
                $"The history holds {snapshots.Length} snapshots; a cycle needs at least {OptimizationDataSplit.MinimumSnapshots} to split into training and validation." +
                (WhyNoSampling() is { } why ? " " + why : ""));

        var notes = new List<string>();
        if (!optimization.Enabled)
            notes.Add("Optimization is disabled (optimization.enabled): no live cycle runs, so nothing would be applied.");
        if (!HasSufficientHistory(snapshots, optimization))
            notes.Add($"The history spans less than {MinimumHistoryFraction:P0} of the {optimization.EffectiveTrainingWindowMins}-minute training window; a scheduled cycle would not run on it yet.");
        if (string.IsNullOrWhiteSpace(optimizer) && ConfiguredMode != Mode)
            notes.Add(RemovedModes.Contains(ConfiguredMode)
                ? $"The configured optimizer mode '{ConfiguredMode}' has been removed, so {DefaultMode} runs."
                : $"The configured optimizer mode '{ConfiguredMode}' is not {DefaultMode}, the only mode, so {DefaultMode} runs.");
        var note = notes.Count > 0 ? string.Join(" ", notes) : null;

        await _dryRunGate.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() => OptimizationCycle.Evaluate(split.Training, split.Validation, optimization, optimizers, _nsd.Get, dryRun: true, DateTime.UtcNow), cancellationToken);
            return note == null ? result : result with { Report = result.Report with { Note = note } };
        }
        finally
        {
            _dryRunGate.Release();
        }
    }

    /// <summary>
    /// One line per node of a proposal that a gate turned down, each proposed value beside the incumbent one it
    /// would have replaced, as the cycle froze and scored it, so a rejection can be diagnosed from the log at the
    /// default level. The last-cycle record (<see cref="LastCycle"/>) has the proposed values too, but not what
    /// they would have replaced, and only the latest cycle.
    /// </summary>
    private void LogRejectedProposal(OptimizerProposalReport p, IReadOnlyDictionary<string, NodeSettings> incumbent)
    {
        if (!_logger.IsEnabled(LogLevel.Information)) return;
        foreach (var (id, proposed) in p.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            var current = incumbent.TryGetValue(id, out var ns) ? ns : _nsd.Get(id);
            _logger.LogInformation("Rejected {Optimizer} {NodeId,-20}: {Values}", p.Optimizer, id, DescribeProposal(proposed, current.Calibration));
        }
    }

    /// <summary>
    /// "absorption=3.10 (was 2.95), rx_adj=3 (was -4), tx_ref=-61 (was unset, scored as -59), error=4.20", leaving out
    /// values not proposed. An unset incumbent value is shown with the default <see cref="OptimizationResults"/>
    /// scores it with, labelled as such so it is not mistaken for a setting the node reported.
    /// </summary>
    internal static string DescribeProposal(OptimizerNodeProposal proposed, CalibrationSettings current)
    {
        // Round first, then + 0.0, so a value that rounds to zero from below (or is already -0) prints as 0, not -0.
        static string F(double v, string format) =>
            (Math.Round(v, format == "0" ? 0 : 2, MidpointRounding.AwayFromZero) + 0.0).ToString(format, CultureInfo.InvariantCulture);
        static string Was(double? v, double scoredAs, string format) => v is { } d ? $"was {F(d, format)}" : $"was unset, scored as {F(scoredAs, format)}";
        var parts = new List<string>();
        if (proposed.Absorption is { } a) parts.Add($"absorption={F(a, "0.00")} ({Was(current.Absorption, OptimizationResults.DefaultAbsorption, "0.00")})");
        if (proposed.RxAdjRssi is { } r) parts.Add($"rx_adj={F(r, "0")} ({Was(current.RxAdjRssi, OptimizationResults.DefaultRxAdjRssi, "0")})");
        if (proposed.TxRefRssi is { } t) parts.Add($"tx_ref={F(t, "0")} ({Was(current.TxRefRssi, OptimizationResults.DefaultTxRefRssi, "0")})");
        if (proposed.Error is { } e) parts.Add($"error={F(e, "0.00")}");
        return parts.Count > 0 ? string.Join(", ", parts) : "no values";
    }

    /// <summary>
    /// Which of the values being applied sit on a configured optimization limit. Checked on what is
    /// actually applied, not the raw fit: rxAdj and txRef are stored as whole dB and absorption is
    /// published to two decimals, so a fit of 29.6 against a limit of 30 lands on the limit.
    /// A limit pair with min == max pins the value on purpose and is not reported.
    /// </summary>
    internal static List<string> LimitsReached(double? absorption, int? rxAdjRssi, int? txRefRssi, ConfigOptimization optimization)
    {
        var reached = new List<string>();

        // Limits are compared in the same rounding as the value, so a limit that is not a whole dB
        // (or not two decimals of absorption) still counts as reached by the nearest value that can be applied.
        void Check(string name, double? value, double min, double max, string format)
        {
            if (value == null || max <= min) return;
            var shown = value.Value.ToString(format, CultureInfo.InvariantCulture);
            if (value <= min) reached.Add($"{name}={shown} ({name}_min)");
            else if (value >= max) reached.Add($"{name}={shown} ({name}_max)");
        }

        static double Published(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        Check("absorption", absorption.HasValue ? Published(absorption.Value) : null,
            Published(optimization.AbsorptionMin), Published(optimization.AbsorptionMax), "0.00");
        Check("rx_adj_rssi", rxAdjRssi, Math.Round(optimization.RxAdjRssiMin), Math.Round(optimization.RxAdjRssiMax), "0");
        Check("tx_ref_rssi", txRefRssi, Math.Round(optimization.TxRefRssiMin), Math.Round(optimization.TxRefRssiMax), "0");
        return reached;
    }
}
