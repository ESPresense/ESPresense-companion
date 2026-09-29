#!/usr/bin/env python3
"""Regression tests for the CMP-1 fusion prototype (ESPA-197).

Two independent claims, tested separately on purpose (see occupancy_fusion.py
SCOPE BOUNDARY docstring -- do not conflate them):

1. The CSI motion/energy feature separates vacant vs. occupied-still on the
   SYNTHETIC corpus. This is a weak claim: the synthetic generator
   (ecf1.py:gen_fixture) directly injects a larger amplitude spread for
   occupied scenes, so a variance-based feature recovering that is expected,
   not evidence the feature works on real CSI. It exercises the real
   plumbing (ECF1 parsing, per-subcarrier variance, thresholding) end to end.
2. The fuse() truth table behaves as specified, independent of any corpus.
"""
import glob
import os
import unittest

import occupancy_fusion as of

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, "corpus")


class TestCsiMotionScoreOnSyntheticCorpus(unittest.TestCase):
    def test_zero_hz_buckets_have_no_csi_feature(self):
        for path in glob.glob(os.path.join(CORPUS, "*_000hz_*.ecf1")):
            with open(path, "rb") as f:
                buf = f.read()
            self.assertIsNone(of.csi_motion_score(buf), path)

    def test_occupied_scores_above_vacant_every_board_and_rate(self):
        pairs = 0
        for occ_path in glob.glob(os.path.join(CORPUS, "*_occupied-still.ecf1")):
            vac_path = occ_path.replace("_occupied-still.ecf1", "_vacant.ecf1")
            if not os.path.exists(vac_path):
                continue
            with open(occ_path, "rb") as f:
                occ_score = of.csi_motion_score(f.read())
            with open(vac_path, "rb") as f:
                vac_score = of.csi_motion_score(f.read())
            if occ_score is None or vac_score is None:
                continue
            self.assertGreater(occ_score, vac_score, (occ_path, vac_path))
            pairs += 1
        self.assertEqual(pairs, 10, "expected 10 board/rate pairs (2 boards x 5 nonzero rates)")

    def test_classification_matches_ground_truth_on_synthetic_corpus(self):
        files = sorted(glob.glob(os.path.join(CORPUS, "*.ecf1")))
        scored = [of.classify_file(p) for p in files]
        scored = [r for r in scored if r["csi_score"] is not None]
        self.assertEqual(len(scored), 20)
        wrong = [r["file"] for r in scored if not r["correct"]]
        self.assertEqual(wrong, [], "CSI-only classification should match gt on the synthetic corpus")


class TestFusionRule(unittest.TestCase):
    """fuse() truth table -- independent of ECF1/corpus, see module docstring."""

    def test_both_signals_agree_occupied_is_high_confidence(self):
        occ, conf, _ = of.fuse(csi_score=0.9, ble_present=True)
        self.assertTrue(occ)
        self.assertEqual(conf, "high")

    def test_both_signals_agree_vacant_is_high_confidence(self):
        occ, conf, _ = of.fuse(csi_score=0.01, ble_present=False)
        self.assertFalse(occ)
        self.assertEqual(conf, "high")

    def test_device_free_occupant_csi_only_still_reports_occupied(self):
        # the whole reason CSI exists: BLE sees nothing, CSI sees motion/energy.
        occ, conf, reason = of.fuse(csi_score=0.9, ble_present=False)
        self.assertTrue(occ)
        self.assertIn("no tracked device", reason)

    def test_ble_present_wins_over_quiet_csi(self):
        # a stationary/asleep tracked occupant must not be fused away.
        occ, conf, _ = of.fuse(csi_score=0.01, ble_present=True)
        self.assertTrue(occ)

    def test_no_signal_at_all_defaults_to_not_occupied_low_confidence(self):
        occ, conf, _ = of.fuse(csi_score=None, ble_present=None)
        self.assertFalse(occ)
        self.assertEqual(conf, "low")

    def test_missing_csi_reading_is_not_conflated_with_zero_motion(self):
        # None (no reading) and 0.0 (a real, very-quiet reading) must not
        # produce the same confidence when BLE also has nothing to say.
        occ_none, conf_none, _ = of.fuse(csi_score=None, ble_present=None)
        occ_zero, conf_zero, _ = of.fuse(csi_score=0.0, ble_present=None)
        self.assertEqual((occ_none, occ_zero), (False, False))
        self.assertNotEqual(conf_none, "high")
        self.assertNotEqual(conf_zero, "high")


if __name__ == "__main__":
    unittest.main()
