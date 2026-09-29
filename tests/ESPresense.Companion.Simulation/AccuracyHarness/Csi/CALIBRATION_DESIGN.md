# CMP-3: per-room calibration & drift handling (ESPA-199)

Companion workstream 3 of the CSI occupancy prototype (ESPA-195, board-approved CONDITIONAL GO). This workstream exists to answer kill criterion #1 honestly, not to paper over it: **"per-room calibration/drift burden may be too high to ship."** This document is that answer, feeding EVAL-1 (ESPA-200).

## Status

Design + a synthetic drift-scenario simulation (`adaptive_baseline.py`, `test_adaptive_baseline.py`, 8/8 passing). **No real household and no real CSI corpus exist yet** — FW-1/ESPA-196 is still on hardware procurement per the ESPA-195 board approval. Everything below is either (a) first-principles reasoning about CSI/multipath physics and the algorithm's own logic, which does not need real data to evaluate, or (b) evidence from the *existing* ESPresense BLE calibration system, which is a real, load-bearing analog already exercised by thousands of real households. Nothing below is a real-CSI accuracy claim — CMP-1's FUSION_DESIGN.md already establishes that discipline and this document keeps it.

## Question 1: what does initial per-room calibration require from a real household?

We don't have a CSI-specific household yet, but we don't need one to answer the load-bearing part of this question, because **ESPresense already ships a per-node calibration system for BLE distance/absorption**, and the community has been telling us for years what that burden looks like:

- [companion#628](https://github.com/ESPresense/ESPresense-companion/issues/628) ("Calibration only uses Rx RSSI adjustments") — a user reports the automatic absorption calibration ran for **multiple days** before it started adjusting anything, describes getting "lost in the calibration sections" trying to debug it, and had to **rethink appliance/furniture placement** (monitors, fridges) because they block signal enough to matter. A second user in the thread assumed *user error* when their Tx values never moved from default — i.e. the system's own state was not legible enough for a technical user to tell "working slowly" apart from "broken."
- [companion#692](https://github.com/ESPresense/ESPresense-companion/issues/692) / [firmware#692](https://github.com/ESPresense/ESPresense/issues/692) ("Calibration help") and [firmware#324](https://github.com/ESPresense/ESPresense/issues/324) ("Directions or Manual to assist with Calibration of multiple devices") are both multi-comment threads asking, in effect, "what am I supposed to do."

That's the burden of calibrating **one** signal (BLE path-loss/absorption per node) that is comparatively well-behaved (RSSI is a single scalar, physically simple). CSI baseline calibration is a **second, orthogonal calibration surface** on top of that: it's not about antenna placement or transmit power, it's about each node's ambient multipath noise floor, which depends on furniture, wall materials, and what else is transmitting on 2.4 GHz in that specific room. A household adopting CSI occupancy pays the existing BLE calibration tax *and* a new one.

Given the adaptive-baseline design below, the honest per-room breakdown is:

| Room class | Manual steps required | Time to first calibrated threshold |
|---|---|---|
| Rooms with regular BLE-tracked traffic (living room, main bedroom, kitchen) | **None**, if the auto-recalibration path (Q3) works | Simulated: ~20 BLE-confirmed-vacant windows. At a plausible real cadence (room empties for a few minutes several times a day), that's **on the order of 1 day**, not the multi-day BLE absorption experience reported above. |
| Rooms nobody carrying a tracked device passes through (bathroom, nursery, a memory-care bedroom, hallway) | **A manual "teach this room empty" step is unavoidable** — see Q3 | Unbounded without that manual step — the simulation shows it literally never completes on its own (`test_permanently_device_free_room_never_self_calibrates`). |

The second row is the uncomfortable finding: it is not a small edge case. It is disproportionately the **exact room class the mission targets** — elopement and fall response are about people who often are *not* carrying a phone (children, memory-care residents), and per-room HVAC cares most about the rooms people spend unattended time in. The rooms where CSI has the most reason to exist are the rooms its own most natural auto-calibration mechanism cannot reach.

## Question 2: how fast does the CSI baseline drift, and what cadence does that force?

We don't have a measured drift-rate number (that requires FW-1's real corpus, running for weeks in a real home — out of scope for this workstream by itself). What we can say with confidence, from RF/multipath physics and from how esp-radar/esp-csi style detectors behave in the wider community, is the *shape* of drift, which the simulation models as three distinct regimes:

1. **Discrete step changes** (furniture moved, a door left open/closed differently, someone's new WiFi-connected appliance shows up) — the noise floor jumps and stays at a new level. `test_furniture_move_step_change_reconverges` shows the adaptive floor re-converges to a step change using only the BLE-vacant windows that occur naturally afterward, with no special-cased "recalibration event" needed — convergence is governed by the EWMA smoothing constant (`floor_alpha=0.05`, i.e. ~20 anchor samples to mostly settle, matching `min_anchor_samples`).
2. **Slow continuous drift** (seasonal humidity/temperature, HVAC vent cycling changing the room's air-path characteristics) — `test_gradual_seasonal_drift_tracks_without_false_alarms` shows the same EWMA tracks a slow ramp within a tight margin, provided BLE-vacant windows keep recurring at a reasonable cadence (modeled every 4th sample here).
3. **Router/AP-side changes** (channel switch, firmware update changing TX power or rate adaptation behavior) — these look like a step change from the node's perspective, same handling as (1), *but* they are invisible to the household (no furniture visibly moved) and therefore the most likely to go unnoticed if a room's floor mis-tracks — which only matters if that room lacks a BLE anchor (see below).

**The cadence this forces is conditional, not a single number:** in a BLE-anchored room, re-calibration is continuous and free — every confirmed-vacant window is a free recalibration tick, so there is no meaningful "cadence" a household needs to think about. In a device-free room, there is no automatic re-anchoring at all, so *any* of the three drift regimes above silently invalidates the threshold until a human notices misbehavior (false "occupied" HVAC running all night, or worse, false "vacant" in a safety-relevant room) and manually recalibrates — which is precisely the manual-chore failure mode kill criterion #1 is asking about, and precisely the room class from Q1 that can't avoid it.

## Question 3: is there a viable auto-recalibration path, or does this stay a manual chore?

**Partial.** The design in `adaptive_baseline.py` (`AdaptiveBaseline`) is a viable, testable auto-recalibration path *conditioned on an existing BLE anchor being available in that room*:

- It replaces CMP-1's fixed global `CSI_OCCUPIED_THRESHOLD = 0.08` with a per-node floor learned only from windows where the companion's existing BLE fusion signal independently reports "confirmed vacant" (`blePresent == false`), scaled by a margin (`floor * 1.6`, itself a placeholder pending real-corpus tuning, same honesty caveat CMP-1 already applies to its own constant).
- It is deliberately immune to the obvious failure mode of an unsupervised approach: a percentile-of-recent-history tracker would creep upward during any long occupied stretch (a napping/reading occupant starts looking like "normal"), silently raising the threshold and producing false "vacant" reads — the worse failure mode for an HVAC/safety-adjacent signal per FUSION_DESIGN.md's own stated bias. Anchoring only on BLE-confirmed-vacant windows structurally can't creep that way: `test_continuously_occupied_room_does_not_creep` shows the floor simply never updates (and the detector correctly stays un-calibrated / falls back to the global placeholder) rather than drifting toward "occupied is normal."
- Its cost is exactly the mirror image of that safety: a room with **no** BLE-confirmed-vacant windows — ever — never calibrates, full stop (`test_permanently_device_free_room_never_self_calibrates`, 10,000 simulated samples, still uncalibrated). This is not a slow-convergence problem that more time fixes; the anchor condition is structurally never met.

So the honest answer is: **auto-recalibration is real and it eliminates the manual chore for the majority of monitored rooms** — anywhere the household's existing BLE-tracked devices already pass through regularly. It **does not** eliminate the manual chore for permanently device-free rooms, and does not have a good unsupervised substitute for those rooms without accepting the creep risk it's specifically designed to avoid. For that room class, the honest options are:

- an install-time wizard step ("leave this room empty for N minutes") — a real, if one-time, manual step, same shape as the BLE calibration UI's existing per-node absorption tuning; or
- a scheduled known-vacant window the installer configures once (e.g. "nobody is ever in the nursery 1–4am") as a synthetic anchor; or
- accept that those specific rooms run on CMP-1's global placeholder threshold indefinitely, with the accuracy risk that implies, and flag it to the household explicitly rather than silently.

None of those is "no burden." All of them are cheaper than re-running full manual calibration on a fixed schedule, which is the alternative this design is being weighed against.

## Recommendation to EVAL-1 (ESPA-200)

Don't kill on this workstream alone — real separability of vacant/occupied on actual multipath is still an open question pending FW-1's measured corpus, and that number matters more to the kill decision than calibration mechanics do. But feed EVAL-1 this specific, asymmetric finding: **calibration burden is low-to-zero in BLE-instrumented rooms and structurally non-zero in device-free rooms — and the device-free rooms are disproportionately the ones the mission (elopement, fall response, unattended-room HVAC) cares about most.**

Concretely:
1. Don't ship a blanket "walk away, it just calibrates itself" claim. Ship "auto-calibrates in rooms with existing tracked-device traffic; requires a one-time install step in fully device-free rooms," and build that install step (this doc does not scope its UI).
2. Gate the "no accuracy claim yet" caveat from FUSION_DESIGN.md through to this document: `margin_ratio=1.6` is as much a placeholder as CMP-1's `0.08` was, and needs the same real-corpus recalibration once FW-1 lands.
3. When FW-1's corpus exists, re-run these same five scenario tests against measured drift rates (furniture-move magnitude, real seasonal swing, real router-change frequency) instead of the constructed step sizes used here, and tighten `floor_alpha`/`min_anchor_samples`/`margin_ratio` against real numbers before this goes anywhere near a real household.

## Files

| File | Role |
|---|---|
| `adaptive_baseline.py` | The `AdaptiveBaseline` per-node floor-learning algorithm + a deterministic synthetic drift-scenario simulator. |
| `test_adaptive_baseline.py` | The five drift-scenario tests referenced above, plus unit tests of the anchor/threshold contract. |
| `CALIBRATION_DESIGN.md` | This document. |

```bash
# run the regression suite
cd tests/ESPresense.Companion.Simulation/AccuracyHarness/Csi
python3 -m unittest -v test_adaptive_baseline
```
