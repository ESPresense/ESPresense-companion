#!/usr/bin/env python3
"""Regression tests for adaptive_baseline.py (CMP-3, ESPA-199).

Each test encodes one of the drift scenarios named in the ESPA-199 issue
description (furniture moves, seasonal/router drift, auto-recalibration
viability) plus the continuous-occupancy anti-creep property the design
relies on. These are synthetic-scenario tests of the ALGORITHM's behavior,
not accuracy claims against real CSI -- see CALIBRATION_DESIGN.md.
"""
import unittest

from adaptive_baseline import AdaptiveBaseline, DriftEvent, simulate


class TestAdaptiveBaselineUnit(unittest.TestCase):
    def test_uncalibrated_until_min_anchor_samples(self):
        b = AdaptiveBaseline(min_anchor_samples=5)
        for _ in range(4):
            b.observe(0.045, ble_present=False)
        self.assertFalse(b.is_calibrated)
        self.assertIsNone(b.threshold)
        b.observe(0.045, ble_present=False)
        self.assertTrue(b.is_calibrated)
        self.assertAlmostEqual(b.threshold, 0.045 * 1.6, places=6)

    def test_occupied_and_unknown_windows_never_update_floor(self):
        b = AdaptiveBaseline(min_anchor_samples=1)
        b.observe(0.045, ble_present=False)
        floor_after_anchor = b.floor
        b.observe(0.5, ble_present=True)
        b.observe(0.5, ble_present=None)
        self.assertEqual(b.floor, floor_after_anchor)
        self.assertEqual(b.anchor_samples, 1)

    def test_margin_ratio_must_exceed_one(self):
        with self.assertRaises(ValueError):
            AdaptiveBaseline(margin_ratio=1.0)


class TestDriftScenarios(unittest.TestCase):
    """Scenario A: a BLE-instrumented room (typical living room/bedroom) --
    vacant one window in three, occupied otherwise."""

    def test_cold_start_calibrates_within_expected_anchor_budget(self):
        def vacancy(i):
            return False if i % 3 == 0 else True

        rows = simulate([], total_samples=200, vacancy_pattern=vacancy)
        first_calibrated = next((i for i, r in enumerate(rows) if r[3] is not None), None)
        self.assertIsNotNone(first_calibrated, "should calibrate given enough vacant windows")
        # default min_anchor_samples=20, vacant every 3rd sample -> ~60 samples
        self.assertLessEqual(first_calibrated, 65)
        final_threshold = rows[-1][3]
        self.assertAlmostEqual(final_threshold, 0.045 * 1.6, delta=0.01)

    def test_furniture_move_step_change_reconverges(self):
        """A step-change in the true floor (furniture rearranged, blocking/
        unblocking a multipath route) must be absorbed by later BLE-vacant
        windows without a manual reset."""

        def vacancy(i):
            return False if i % 3 == 0 else True

        events = [DriftEvent(at_sample=300, new_floor=0.065, label="furniture moved")]
        rows = simulate(events, total_samples=600, vacancy_pattern=vacancy)

        threshold_before_drift = rows[290][3]
        threshold_long_after_drift = rows[590][3]

        self.assertAlmostEqual(threshold_before_drift, 0.045 * 1.6, delta=0.01)
        # must have moved toward the new floor, not stayed pinned to the old one
        self.assertGreater(threshold_long_after_drift, threshold_before_drift)
        self.assertAlmostEqual(threshold_long_after_drift, 0.065 * 1.6, delta=0.01)

    def test_gradual_seasonal_drift_tracks_without_false_alarms(self):
        """Slow drift (temperature/humidity/HVAC-vent seasonal change) modeled
        as many small steps spread across a long window -- the floor should
        track it smoothly rather than needing a discrete re-calibration event."""

        def vacancy(i):
            return False if i % 4 == 0 else True

        events = [DriftEvent(at_sample=i, new_floor=0.045 + 0.00005 * i, label="seasonal") for i in range(0, 4000, 50)]
        rows = simulate(events, total_samples=4000, vacancy_pattern=vacancy)
        final_true_floor = rows[-1][0]
        final_threshold = rows[-1][3]
        self.assertAlmostEqual(final_threshold, final_true_floor * 1.6, delta=0.015)

    def test_continuously_occupied_room_does_not_creep(self):
        """Anti-creep property: a room with a long-resident BLE-tracked
        occupant must never have its floor dragged upward by occupied
        readings -- that would raise the threshold and start swallowing
        real occupancy (the false-vacant failure mode)."""

        def vacancy(i):
            return True  # always BLE-present -- never a confirmed-vacant window

        rows = simulate([], total_samples=500, vacancy_pattern=vacancy)
        self.assertTrue(all(r[3] is None for r in rows))

    def test_permanently_device_free_room_never_self_calibrates(self):
        """The honest headline finding: a room with no BLE-tracked device
        ever (ble_present always None) never accumulates a confirmed-vacant
        anchor, so it never calibrates on its own -- regardless of how long
        it runs. This is exactly the room class (kid's room, memory-care
        bedroom, elopement risk) the CSI signal exists to cover."""

        def vacancy(i):
            return None

        rows = simulate([], total_samples=10_000, vacancy_pattern=vacancy)
        self.assertTrue(all(r[3] is None for r in rows))


if __name__ == "__main__":
    unittest.main()
