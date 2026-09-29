#!/usr/bin/env python3
"""Per-node adaptive baseline calibration for CSI occupancy (CMP-3, ESPA-199).

Companion workstream 3 of the CSI occupancy prototype (ESPA-195). CMP-1's
CsiOccupancyDetector ships a fixed global threshold (CSI_OCCUPIED_THRESHOLD
= 0.08) calibrated against a 24-file *synthetic* corpus, and says outright it
"will not survive contact with a real home" -- see FUSION_DESIGN.md's
"Threshold, honestly" section, written by CMP-1. CMP-3 owns replacing that
fixed global number with something that adapts per node, and this module is
the first candidate design for that replacement.

STATUS: design + synthetic drift-scenario simulation only (ESPA-199). There
is no real household and no real CSI corpus yet -- FW-1/ESPA-196 is still on
hardware procurement (see the ESPA-195 board approval). This module therefore
validates the *algorithm's behavior* against constructed drift scenarios
(furniture move, seasonal drift, router-driven step change, continuous-
occupancy creep, permanently device-free rooms), not real-world accuracy. See
CALIBRATION_DESIGN.md for what this does and does not prove, and for the
household-burden and drift-cadence findings this simulation feeds into EVAL-1.
"""
from dataclasses import dataclass


class AdaptiveBaseline:
    """Learns a per-node "empty room" motion-score floor, and derives an
    occupied threshold from it, instead of using one fixed global constant.

    Design choice: the floor updates ONLY during windows where the existing
    BLE fusion signal (OccupancyFusion's `blePresent`) independently reports
    False -- i.e. the companion's BLE pipeline actively checked this room and
    found no tracked device. It does NOT update on `blePresent in (True,
    None)`.

    Why anchor on BLE-vacant instead of blind percentile-of-recent-history:
    a percentile tracker with no ground truth creeps UP during any long
    occupied stretch (a napping or reading occupant starts looking like "the
    new normal quiet floor"), which silently raises the threshold and swallows
    real occupancy -- exactly the false-negative failure mode FUSION_DESIGN.md
    is built to avoid (a false "vacant" is the worse failure for HVAC-by-
    occupancy and, more importantly, for elopement/fall response). Anchoring
    to BLE-confirmed-vacant windows only breaks that feedback loop.

    The cost of that choice is the headline finding of this workstream: a
    room where BLE never reports a confirmed-vacant window (nobody carrying a
    tracked device ever passes through, e.g. because the whole point of the
    room is that its occupant doesn't carry one) never calibrates by itself.
    See CALIBRATION_DESIGN.md.
    """

    def __init__(self, margin_ratio: float = 1.6, floor_alpha: float = 0.05, min_anchor_samples: int = 20):
        if margin_ratio <= 1.0:
            raise ValueError("margin_ratio must be > 1.0 (threshold must sit above the learned floor)")
        self.margin_ratio = margin_ratio
        self.floor_alpha = floor_alpha
        self.min_anchor_samples = min_anchor_samples
        self.floor: float | None = None
        self.anchor_samples = 0

    def observe(self, motion_score: float, ble_present: bool | None) -> None:
        """Feed one node's motion score for this window, plus the fusion
        layer's BLE presence read for the same room this window (True /
        False / None -- see OccupancyFusion.Fuse's tri-state contract).
        """
        if ble_present is not False:
            return  # only a confirmed-vacant window is a trustworthy anchor
        if self.floor is None:
            self.floor = motion_score
        else:
            self.floor = (1 - self.floor_alpha) * self.floor + self.floor_alpha * motion_score
        self.anchor_samples += 1

    @property
    def is_calibrated(self) -> bool:
        return self.floor is not None and self.anchor_samples >= self.min_anchor_samples

    @property
    def threshold(self) -> float | None:
        """None means "not yet calibrated" -- caller must fall back to
        CsiOccupancyDetector.OccupiedThreshold, the global placeholder, until
        this node accumulates enough BLE-confirmed-vacant anchor samples.
        """
        if not self.is_calibrated:
            return None
        return self.floor * self.margin_ratio


@dataclass
class DriftEvent:
    """A step-change injected into the simulated floor at a given sample index."""
    at_sample: int
    new_floor: float
    label: str


def simulate(
    true_floor_schedule: list[DriftEvent],
    total_samples: int,
    vacancy_pattern,
    noise_amplitude: float = 0.01,
    occupied_bump: float = 0.09,
    seed: int = 20260928,
):
    """Deterministic synthetic simulation (LCG, no RNG module dependency --
    same determinism discipline as QA-1's ecf1.py generator).

    vacancy_pattern(i) -> bool | None : whether room i is BLE-confirmed-vacant
    (False -> vacant window, generates a floor-level reading), occupied (True
    -> generates floor+occupied_bump), or ambiguous (None -> generates a
    floor-level-ish reading but must NOT be used as a calibration anchor).

    Returns per-sample (true_floor, observed_score, ble_present, learned_threshold).
    """
    class LCG:
        def __init__(self, seed):
            self.x = seed & 0xFFFFFFFF

        def next(self):
            self.x = (1103515245 * self.x + 12345) & 0x7FFFFFFF
            return self.x

        def unit(self):
            return (self.next() % 10000) / 10000.0

    rng = LCG(seed)
    baseline = AdaptiveBaseline()
    schedule = sorted(true_floor_schedule, key=lambda e: e.at_sample)
    current_floor = schedule[0].new_floor if schedule and schedule[0].at_sample == 0 else 0.045
    schedule_idx = 1 if schedule and schedule[0].at_sample == 0 else 0

    rows = []
    for i in range(total_samples):
        while schedule_idx < len(schedule) and schedule[schedule_idx].at_sample == i:
            current_floor = schedule[schedule_idx].new_floor
            schedule_idx += 1

        ble_present = vacancy_pattern(i)
        noise = (rng.unit() - 0.5) * 2 * noise_amplitude
        if ble_present is True:
            score = current_floor + occupied_bump + noise
        else:
            score = current_floor + noise

        baseline.observe(score, ble_present)
        rows.append((current_floor, score, ble_present, baseline.threshold))
    return rows
