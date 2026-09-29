# CMP-1: CSI+BLE fusion — room-occupancy signal design (ESPA-197)

Companion workstream 1 of the CSI occupancy prototype (ESPA-195, board-approved CONDITIONAL GO). Scope, source material, and the augmentation thesis are set by the feasibility doc (ESPA-168) and repeated here only where it changes the design: **coarse, device-free room occupancy** (esp-csi/esp-radar style), fusing with — not replacing — the existing BLE trilateration pipeline. Pose, vitals, and through-wall sensing are explicitly out of scope for v1.

## Status

Design + a synthetic-data pass, per this issue's explicit parallel-work allowance (real validation depends on FW-1's measured corpus, not yet available — FW-1/ESPA-196 is still on hardware procurement). Everything below that touches accuracy numbers is qualified as synthetic; do not cite the 100% figure below as real-world accuracy.

## The feature: CSI motion/energy score

`occupancy_fusion.csi_motion_score()` computes, per bucket (one node, one measurement window):

1. Per-subcarrier amplitude `sqrt(I² + Q²)` for every captured CSI frame.
2. Variance of that amplitude across frames, per subcarrier (temporal variance — how much the channel response for that subcarrier moves over the window).
3. Mean variance across subcarriers, `sqrt`'d back to an amplitude-like scale, divided by a fixed reference ceiling to land roughly in `[0, 1]`.

This is the standard esp-radar/esp-csi device-free-presence primitive: a person in the space perturbs the multipath environment (even "still" — breathing, small motion, RF shadowing), which shows up as higher variance in the channel response than an empty room. It does **not** attempt range, angle, pose, or vital-sign extraction — those require far more (real, phase-calibrated) signal processing than this coarse energy statistic, and are out of scope per ESPA-168.

**A missing CSI reading returns `None`, never `0.0`.** A node that's offline or has no CSI this window is a missing feature, not evidence of an empty room — conflating the two biases the signal toward false-vacant, which is the wrong failure mode for an HVAC/safety-adjacent system (see ESPA-195 kill criterion on real-home robustness).

## The fusion rule: `fuse(csi_score, ble_present)`

v1 is a deliberately simple, inspectable decision table — not a learned model:

| BLE | CSI | Result | Confidence |
|---|---|---|---|
| present | hit | occupied | high (agree) |
| present | quiet/unavailable | occupied | medium (trust BLE, the more direct signal) |
| absent | hit | **occupied** | medium (device-free occupant — the case CSI exists for) |
| absent | quiet | vacant | high (agree) |
| unavailable | hit | occupied | low |
| unavailable | unavailable | vacant | low ("unknown", treated conservatively) |

Rationale for picking a truth table over a model for v1:

- **The named kill criteria are about robustness and drift, not raw accuracy.** A one-line-explainable rule is a rule a maintainer can debug against a real complaint thread (c.f. #1817 "wrong room despite stable coordinates" — the org already has painful experience with opaque distance/room logic that nobody could reason about after the fact).
- **Bias toward not missing an occupant.** Either signal firing is enough to call "occupied" — this is the correct asymmetry for the first target consumer (CMP-2, HVAC-by-occupancy): a false "vacant" turns off climate control on someone actually in the room, which is a worse outcome than a few extra minutes of HVAC running in an empty room. The mission context (elopement prevention, fall response) makes the same asymmetry argument even more strongly for any later consumer of this signal.

## Threshold, honestly

`CSI_OCCUPIED_THRESHOLD = 0.08` is picked by inspecting the 24-file synthetic corpus: vacant scenes cluster at 0.043–0.047, occupied-still clusters at 0.117–0.131, consistently across both boards (esp32, esp32-s3) and all five nonzero capture rates (10/20/30/50/100 Hz). The midpoint gives full margin on this corpus.

**This is a synthetic-corpus number and will not survive contact with a real home.** Real environments have per-node noise floors that vary with placement, furniture, other 2.4 GHz traffic, and drift over time/temperature — exactly kill criterion #1 (per-room calibration/drift burden). CMP-3 owns replacing this fixed global threshold with a per-node adaptive baseline (e.g., a rolling "empty room" floor learned during known-vacant periods, or a percentile-based auto-threshold). This module is structured so that swap is local: `CSI_OCCUPIED_THRESHOLD` becomes a per-node value passed into `fuse()`/`csi_motion_score()`'s caller, no other logic changes.

## What was actually validated here — and what wasn't

| Claim | Validated how | Real-world signal? |
|---|---|---|
| CSI parsing + per-subcarrier variance pipeline runs end-to-end on real ECF1 bytes | `test_occupied_scores_above_vacant_every_board_and_rate`, `test_classification_matches_ground_truth_on_synthetic_corpus` — 20/20 correct | No — the synthetic generator (`ecf1.py:gen_fixture`) injects a bigger amplitude spread for occupied scenes by construction, so recovering that is expected plumbing behavior, not evidence the feature generalizes to real multipath |
| `fuse()` truth table matches the spec above, independent of any corpus | `TestFusionRule` (6 cases) | N/A — pure logic test |
| BLE+CSI fusion produces better real-world accuracy than either alone | **Not tested** | Needs FW-1's real corpus + a real per-room BLE presence trace, neither exists yet |

The 100% figure is a smoke test that the wiring works, not a performance claim. The scope-boundary note in `occupancy_fusion.py`'s docstring exists because ECF1's `REC_BLE` records model a fixed *reference advertiser* for the QA-1 coexistence harness (ESPA-173) — they are not a real per-room presence signal, and must not be wired into `fuse()` as if they were.

## Rollout plan (not yet started)

1. **FW-1 lands a real corpus** (measured CSI on the actual hardware, both boards, both scenes at minimum) → re-run `csi_motion_score()` against it, recheck whether variance-based energy still separates vacant/occupied on real multipath, and recalibrate or replace the threshold.
2. **Companion-side ingestion**: the production consumer is C# (`ESPresense.Companion`), not this Python prototype — this module's job is to prove the feature/rule before committing to a C# implementation, same division of labor as the QA-1 harness (Python owns bytes/prototyping, C# owns production truth). A future `csi-replay`-style C# verb in `AccuracyHarness` is the natural landing spot, deferred to when a real corpus exists (see QA-1's README "Status: SYNTHETIC interim corpus").
3. **Per-node calibration** (CMP-3) replaces the fixed threshold before this ships to any real household.
4. **CMP-2** consumes the fused `(occupied, confidence)` output for HVAC automation once 1–3 land.

## Files

| File | Role |
|---|---|
| `occupancy_fusion.py` | The feature (`csi_motion_score`) + fusion rule (`fuse`) + a CLI that classifies a corpus directory and reports synthetic-corpus accuracy. |
| `test_occupancy_fusion.py` | Regression tests — the two claims above, kept separate. |
| `FUSION_DESIGN.md` | This document. |

```bash
# classify the whole synthetic corpus, print per-file results + accuracy to stderr
python3 occupancy_fusion.py

# run the regression suite
python3 -m unittest -v test_occupancy_fusion
```
