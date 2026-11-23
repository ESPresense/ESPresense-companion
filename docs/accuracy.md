# ESPresense positioning accuracy — public baseline

This is the first publicly tracked accuracy baseline for ESPresense, running
the **real** Companion `ILocate` implementations. It is simulated, not
measured on hardware, and its purpose is to give the project a number to
defend and a number to regress against. Real-hardware in-the-loop
measurements will follow in the same report format.

The harness lives at `tests/ESPresense.Companion.Simulation/AccuracyHarness/`.
Re-running it on this revision should reproduce the numbers below within
a small tolerance (seeded RNG, see `--seed`).

A short history: the [ESPresense#2318](https://github.com/ESPresense/ESPresense/pull/2318)
harness on the firmware side was a pure-Python lower bound, deliberately
implemented without Companion's weighting or scale correction so a v1
number could ship before the Companion CLI was ready. That CLI landed in
[Companion#1541](https://github.com/ESPresense/ESPresense-companion/pull/1541)
+ [#1579](https://github.com/ESPresense/ESPresense-companion/pull/1579),
and on the Tech Lead's call the harness was rebuilt here in C# so it
calls the real product. That removes the Python ↔ C# residual-form drift
risk and replaces the lower bound with a baseline.

## How to reproduce

```bash
dotnet build -c Release tests/ESPresense.Companion.Simulation/ESPresense.Simulation.csproj
dotnet run --no-build -c Release --project tests/ESPresense.Companion.Simulation -- \
  accuracy baseline \
  --output tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/baseline-v1.json \
  --markdown tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/baseline-v1.md
```

Default seed is `20260530`. The harness is single-process, in-repo, no
extra runtime dependencies on top of the existing .NET 8 SDK.

## What the baseline covers

A green tick from `.github/workflows/accuracy-harness.yml` means only as
much as the baseline measures, so here is exactly what that is.

**Locators — every multilateralizer in `src/Locators/` is in the
baseline**, on every scenario:

| Baseline `locator` | Config key | Notes |
|---|---|---|
| `NelderMead` | `nelder_mead` | the Companion default |
| `Bfgs` | `bfgs` | |
| `Mle` | `mle` | |
| `MultiFloor` | `multi_floor` | solver only; see the multi-floor gap below |
| `NadarayaWatson` | `nadaraya_watson` | hull-bounded; read "What the numbers mean" before comparing it |
| `GaussNewton` | *(none)* | has no config key, so no deployment can enable it; see below |

`GaussNewtonMultilateralizer` is not in `ScenarioFactory.All()` and
`ConfigLocators` has no alias for it, so it cannot be turned on in
`config.yaml`. The harness scores it anyway — it is cheap, and if it is
ever wired up the baseline is already there — but **its numbers describe
code no user runs.** Do not quote them as ESPresense accuracy.

`AnchorLocator` and `NearestNode` are **not** in the baseline and will not
be: neither solves a position from ranges (one pins a device to a fixed
point, the other reports the nearest node's own coordinates), so a
positioning-error number for them would not measure a solve. They are
listed by name in `LocatorCoverage.NotScored`, and changes to those two
are not covered by this guard.

This list is enforced, not documentation: `accuracy check` reflects over
every concrete `ILocate` in ESPresense.Companion and fails if one is
neither scored by a `LocatorKind` nor declared unscorable. A new locator
cannot land behind a green accuracy tick.

**Node counts — 3 and 4.** Every scenario is 3 or 4 stations. Nothing in
the baseline exercises 1, 2, or 5+ nodes. Installs denser than 4 nodes
per floor are common and are **not** measured here.

**Floors — one.** The scenario schema is a single floor with axis-aligned
rooms (`bounds` is x/y only). `multi_floor` is scored, so a change to its
solve, its weighting, or its `MinimumReadings` gate moves the baseline and
fails the guard — but its *floor selection* (`SpatialUtils.FindFloorsAt`,
the z-mirror behaviour that motivates the four-reading minimum) is **not**
measured, because no scenario has a second floor. A genuine multi-floor
scenario is the next thing this harness needs.

## Scenarios: tier-1 ESP32, 6m × 4m two-room residential

All three scenarios share one geometry — a 6m × 4m floor divided into a 4m
living room and a 2m bedroom, ESP32 nodes in the corners, device carried
at 1.0m — so their numbers are directly comparable:

- `baseline_4node_6x4m`: 4 nodes, ceiling-mounted at 2.4m
- `plug_height_4node_6x4m`: 4 nodes, plugged into wall outlets at 0.3m
- `sparse_3node_6x4m`: `baseline_4node_6x4m` with `node-bedroom-se`
  removed — 3 nodes, ceiling-mounted at 2.4m. Sparse installs are the
  common case in real homes, and three ranges are the point at which an
  unbounded 3D solve can no longer resolve its z mirror.

Two ground-truth tracks:

- `diagonal_walk`: corner-to-corner, 61 samples
- `living_to_bedroom`: a transit straight through the room boundary at
  x=4.0m, 121 samples — explicitly designed to exercise the failure mode
  reported in [#1817](https://github.com/ESPresense/ESPresense/issues/1817)
  (room-allocation flipping at the wall)

Noise profile applied to RSSI (dB) before the firmware's log-distance
formula converts to meters:

- Gaussian σ = **2.5 dB**
- Multipath bursts: **10%** of samples take a **-6 dB** attenuation hit

## Baseline (v1)

All 18 rows (3 scenarios × 6 locators), with per-station outage sweeps,
live in
`tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/baseline-v1.md`
— that file is the baseline, and it is what CI compares against. Summary:

#### `baseline_4node_6x4m` — 4 nodes, ceiling 2.4m

| Locator | `diagonal_walk` n | median | p95 | mis-id | `living_to_bedroom` n | median | p95 | mis-id |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `NelderMead` | 61 | 0.95 | 1.71 | 23.0% | 121 | 0.72 | 1.76 | 32.2% |
| `Bfgs` | 61 | 0.99 | 1.75 | 18.0% | 121 | 0.78 | 2.07 | 32.2% |
| `Mle` | 61 | 0.96 | 1.75 | 23.0% | 121 | 0.75 | 1.77 | 31.4% |
| `MultiFloor` | 61 | 0.93 | 1.76 | 27.9% | 121 | 0.69 | 1.83 | 29.8% |
| `NadarayaWatson` | 61 | 0.66 | 1.05 | 9.8% | 121 | 0.59 | 1.25 | 24.8% |
| `GaussNewton` † | 61 | 1.10 | 1.97 | 11.5% | 121 | 0.96 | 2.33 | 25.6% |

#### `plug_height_4node_6x4m` — 4 nodes, plug height 0.3m

| Locator | `diagonal_walk` n | median | p95 | mis-id | `living_to_bedroom` n | median | p95 | mis-id |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `NelderMead` | 61 | 0.71 | 1.16 | 6.6% | 121 | 0.61 | 1.34 | 20.7% |
| `Bfgs` | 61 | 0.74 | 1.30 | 6.6% | 121 | 0.65 | 2.00 | 20.7% |
| `Mle` | 61 | 0.72 | 1.22 | 6.6% | 121 | 0.62 | 1.45 | 19.8% |
| `MultiFloor` | 61 | 0.74 | 1.61 | 19.7% | 121 | 0.62 | 1.81 | 23.1% |
| `NadarayaWatson` | 61 | 0.56 | 1.01 | 3.3% | 121 | 0.51 | 1.08 | 21.5% |
| `GaussNewton` † | 61 | 0.83 | 1.86 | 4.9% | 121 | 0.80 | 2.02 | 23.1% |

#### `sparse_3node_6x4m` — 3 nodes, ceiling 2.4m

| Locator | `diagonal_walk` n | median | p95 | mis-id | `living_to_bedroom` n | median | p95 | mis-id |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `NelderMead` | 61 | 0.91 | 2.09 | 6.6% | 121 | 0.89 | 2.16 | 28.9% |
| `Bfgs` | 61 | 1.18 | 3.51 | 13.1% | 121 | 1.32 | 2.53 | 27.3% |
| `Mle` | 61 | 0.91 | 2.11 | 6.6% | 121 | 0.91 | 2.61 | 26.4% |
| `MultiFloor` | **0** | no fix | no fix | — | **0** | no fix | no fix | — |
| `NadarayaWatson` | 61 | 0.86 | 2.04 | 8.2% | 121 | 1.36 | 2.27 | 29.8% |
| `GaussNewton` † | 61 | 1.04 | 2.77 | 3.3% | 121 | 1.43 | 2.17 | 20.7% |

Medians and p95 in metres; `n` is the number of samples that produced a
fix out of the track's sample count, so `n` below the track length means
the locator declined to position the device.
† `GaussNewton` has no config key and no deployment can run it — see
"What the baseline covers".

### What the numbers mean

- **Mounting height matters.** With ceiling nodes the device sits 1.4m
  below every node, so every range includes a 1.4m vertical leg the
  solver has to separate from the horizontal distance: median 0.95m and
  room mis-id 23% on `diagonal_walk`. Plug-height nodes sit 0.7m below
  the device: median 0.71m, room mis-id 6.6%.
- **Median ~0.6–1.0m** — most samples place the device within one metre
  of where it actually is, on a 6m × 4m floor.
- **Room mis-id on `living_to_bedroom`** — that track is *deliberately* a
  transit through the boundary at x=4.0m, so a large fraction of samples
  sit within 10cm of the wall. Its mis-id is high by design; the
  `diagonal_walk` track, which spends almost no time near a boundary, is
  the more honest indicator of normal use.
- **Outage sweep**: dropping a single station raises p95 error in every
  case, from a few percent up to roughly double.
- **`multi_floor` reports no position on 3 nodes, by design.**
  `MultiFloorMultilateralizer.MinimumReadings = 4` (see
  [#1916](https://github.com/ESPresense/ESPresense-companion/pull/1916)):
  its solve has four unknowns (x, y, z, scale) and is unbounded in z, so
  three ranges from near-coplanar nodes leave the z mirror unresolved.
  That row is pinned at `n = 0` on purpose. Lowering the constant to 3
  makes the row solve at median 1.18m / p95 2.90m on `diagonal_walk` and
  median 1.10m / p95 3.26m on `living_to_bedroom`, against
  `nelder_mead`'s 0.91m / 2.09m and 0.89m / 2.16m on the same geometry —
  a visibly worse tail from an unbounded solve. **This is the accuracy
  that the four-reading minimum trades away**, and it is why the row is
  in the baseline: change the constant in either direction and the guard
  goes red until the number is re-published.
- **`multi_floor` on 4 nodes has no outage sweep**, and that is a result,
  not a gap. Dropping any one of four stations leaves three, which is
  below `MinimumReadings`, so every outage drop reports no fix. A 4-node
  `multi_floor` install loses the device's position whenever a single
  node goes quiet; `nearest_node` is the fallback that catches it.
- **`nadaraya_watson` looks best here, and the geometry flatters it.** It
  is a distance-weighted average of node positions, so its estimate can
  never leave the convex hull of the nodes it heard. On these scenarios
  the nodes sit in the room's corners and both tracks stay inside that
  hull, so a structurally impossible failure mode (flying outside the
  room) is excluded for it and not for the iterative solvers. Read its
  advantage as "bounded on this layout", not as "more accurate".

Earlier revisions of this baseline put the device at the same 2.4m as the
nodes (ranges were purely horizontal), which reported a 0.60m median and
6.6% mis-id for ceiling mounting. That height is unrealistic for a carried
device, and it hid the cost of the vertical offset.

The Python lower bound
[ESPresense#2318](https://github.com/ESPresense/ESPresense/pull/2318)
published median 0.78–0.82m and room mis-id 29.5–33.1% with purely
horizontal ranges. The real `NelderMead` locator beat it comfortably under
that same assumption (0.60m, see above), as expected: it brings weighting,
scale correction, and Pearson confidence pruning that the stub did not.
The ceiling-mounted figures above are not comparable to it, because they
add the vertical offset the stub never modelled.

## What this baseline does NOT model (blind spots)

These are deliberate, documented v1 limitations.

1. **No real RF.** Noise is injected as Gaussian RSSI jitter plus a
   single-channel multipath burst model. Real deployments see
   board-to-board variance, antenna pattern asymmetry, body absorption,
   and bluetooth-channel-dependent fading that this harness ignores.

2. **No per-board RSSI variance.** Every station uses the same
   `cal_rssi = -59 dBm` and `absorption = 3.0`. Tier-1 hardware variants
   (ESP32, ESP32-C3, ESP32-S3, ESP32-C6) have measurable receive-side
   differences. Per-board calibration models will be added when we have
   on-hardware capture data to fit them against.

3. **2D scoring, fixed heights.** Ground-truth points are 2D (x, y) at a
   fixed device height (`device_z`, 1.0m in all three scenarios); the solver
   runs `ILocate` in 3D against the stations' real heights, and error is
   scored in 2D. Real devices move vertically (pocket, hand, bedside).

4. **No Kalman / hysteresis between samples.** The harness re-uses one
   `ILocate` device across the track so its internal Kalman filter sees
   continuity, but no smoothing is applied on top. This is the right
   layer for catching firmware- and solver-level regressions but is
   noisier than what an end user with `away_timeout` smoothing sees.

5. **Single-floor, axis-aligned rooms — so `multi_floor`'s floor
   selection is unmeasured.** The scenario schema has one floor and
   rectangular rooms; polygon rooms (also supported by Companion) and any
   second floor are out of scope for v1. The `multi_floor` *locator* is
   scored, so its solve, its weighting and its `MinimumReadings` gate are
   all under the guard — but `SpatialUtils.FindFloorsAt` and the
   wrong-floor failures that motivate it are not. Reading a green tick as
   "multi-floor positioning is fine" is not supported by this baseline.
   Adding a two-floor scenario is the top item on the harness backlog.

6. **Node counts are only 3 and 4.** Nothing here covers a 1- or 2-node
   install (where `nearest_node` owns the outcome, not a multilateralizer)
   or a dense 5+ node floor. A change that only shows up at those counts
   will pass this guard.

7. **Noise on the dB side is intentional.** Modelling Gaussian noise on
   the *meter* side instead would understate the room-misid risk at
   boundaries, because the log-distance asymmetry means a 2.5 dB hit at
   short range turns into a much larger meter-side error than at long
   range. The whole point of feeding noise back through the same
   firmware `pow(10, ...)` formula is to keep that asymmetry honest.

8. **Inclusive room bounds, first-match wins.** A point exactly on the
   shared `x=4` boundary belongs to whichever room is declared first in
   the scenario JSON. This is deterministic but a future contributor
   should not "fix" it by making bounds half-open — the failure-mode
   point of the `living_to_bedroom` track depends on us being honest
   about boundary samples.

9. **Node telemetry is faked, which matters for `nadaraya_watson`.** It
   divides by the number of *online* nodes on the floor to compute
   confidence, and the harness's `MockNodeTelemetryStore` reports every
   node online. So its confidence is computed against the full station
   list even during an outage drop. That is the harness's stated model —
   a station that did not report is an outage, not an offline node — but
   it means `nadaraya_watson` confidence is not measured against a real
   telemetry feed.

## How to add a scenario

Drop a JSON file under `tests/ESPresense.Companion.Simulation/AccuracyHarness/Scenarios/`
in the same shape as `baseline_4node_6x4m.json`, then run:

```bash
dotnet run --no-build -c Release --project tests/ESPresense.Companion.Simulation -- \
  accuracy baseline --scenario your_scenario.json
```

To put it **under the CI guard** rather than just run it once, add the
filename to `AccuracyCli.DefaultScenarios` and regenerate the baseline.
`accuracy baseline` and `accuracy check` read that same list on purpose:
if they could disagree, `check` would compare a subset against the full
baseline and pass on rows it never computed.

Open a PR with the JSON file, the noise profile used, and the resulting
report. Community-submitted scenarios are welcome.

## Regressions

The CI workflow at `.github/workflows/accuracy-harness.yml` re-runs the
baseline on every PR that touches the locator code path, the firmware
distance formula in the harness, or the harness itself, and runs the
`accuracy check` subcommand which compares the new report against the
checked-in baseline within a **±5% relative tolerance** (with a 0.01m
absolute floor for near-zero metrics). A drift outside that tolerance
fails CI; either the change is a real regression, or the PR description
must call it out, re-run the harness, and commit the updated baseline
under `tests/ESPresense.Companion.Simulation/AccuracyHarness/Reports/`.

**Scope of the guard is the scope of the baseline** — see "What the
baseline covers". The workflow triggers on all of `src/Locators/**`, so
without a baseline row for a locator it would run on changes to that
locator and only ever be green. `accuracy check` therefore starts by
reflecting over every concrete `ILocate` in ESPresense.Companion and
failing if one is neither scored by a `LocatorKind` nor listed in
`LocatorCoverage.NotScored` with a reason. Adding a locator to
`src/Locators/` turns CI red until it is either baselined or explicitly
declared unscorable.

Sample counts are compared **exactly**, not within tolerance, so a
locator that stops producing a fix fails the guard even if the samples it
still solves are as accurate as before. That is what pins
`MultiFloorMultilateralizer.MinimumReadings`: the `sparse_3node_6x4m` ×
`MultiFloor` row is `n = 0`, and changing the constant in either
direction moves `n` and turns CI red until the new number is published
here.

The tolerance comparison is intentional. A byte-diff guard would flake
on libm differences across runners, on .NET SDK patch revs, and on any
unrelated optimizer change inside `MathNet.Numerics`. The locker we want
is "did we move the public number", not "did we change a digit somewhere
deep in the output".
