#!/usr/bin/env python3
"""CI regression suite for the ECF1 CSI replay fixture (QA-1 / ESPA-173).

stdlib ``unittest`` only -- no pip install, so the CI job is a bare
``python -m unittest`` with zero setup friction. Run locally with:

    python3 -m unittest -v test_csi_replay
"""
import glob
import json
import os
import struct
import tempfile
import unittest

import ecf1
import ecf1_adapter
import csi_replay

HERE = os.path.dirname(os.path.abspath(__file__))
CORPUS = os.path.join(HERE, "corpus")
GOLDEN = os.path.join(HERE, "golden_digests.json")


def corpus_files():
    return sorted(glob.glob(os.path.join(CORPUS, "*.ecf1")))


class TestCorpusIntegrity(unittest.TestCase):
    def test_corpus_present(self):
        files = corpus_files()
        self.assertEqual(len(files), 24,
                         "expected the 24-file synthetic corpus (2 boards x 6 rates x 2 scenes)")

    def test_every_file_validates(self):
        """Every corpus file parses under the independent ecf1 container validator,
        and each CSI record's payload matches the header-declared subcarrier count."""
        for p in corpus_files():
            h, counts, stat = ecf1.validate(p)
            self.assertGreaterEqual(sum(counts.values()), 1, "%s: empty" % p)
            self.assertEqual(h["csi_payload_bytes"], 2 * h["n_subcarriers"], p)

    def test_corpus_is_reproducible_from_seed(self):
        """Regenerate the corpus from the seeded generator into a temp dir and
        assert byte-for-byte equality with the checked-in files. Guards against
        silent corpus drift (a changed generator OR a hand-edited fixture)."""
        with tempfile.TemporaryDirectory() as td:
            boards = {0: "esp32", 1: "esp32-s3"}
            rates = [0, 10, 20, 30, 50, 100]
            scenes = {0: "vacant", 1: "occupied-still"}
            for bid, bname in boards.items():
                for rate in rates:
                    for sid, sname in scenes.items():
                        name = "%s_%03dhz_%s.ecf1" % (bname, rate, sname)
                        gen = os.path.join(td, name)
                        ecf1.gen_fixture(gen, bid, rate, sid)
                        checked = os.path.join(CORPUS, name)
                        self.assertTrue(os.path.exists(checked), "missing %s" % name)
                        with open(gen, "rb") as a, open(checked, "rb") as b:
                            self.assertEqual(a.read(), b.read(),
                                             "%s drifted from the seeded generator" % name)


class TestReplayDeterminism(unittest.TestCase):
    def test_replay_is_deterministic(self):
        """Same file + same rate replayed twice -> identical digest."""
        for p in corpus_files():
            d1 = csi_replay.replay_file(p).summary()["digest"]
            d2 = csi_replay.replay_file(p).summary()["digest"]
            self.assertEqual(d1, d2, "non-deterministic replay for %s" % p)

    def test_realtime_pacing_does_not_change_output(self):
        """Wall-clock pacing must not affect the delivered event trace: virtual
        vs realtime (heavily time-scaled so the test stays fast) -> same digest."""
        p = os.path.join(CORPUS, "esp32-s3_030hz_occupied-still.ecf1")
        virtual = csi_replay.replay_file(p).summary()["digest"]
        realtime = csi_replay.replay_file(p, realtime=True, time_scale=1e-4).summary()["digest"]
        self.assertEqual(virtual, realtime)

    def test_rate_actually_influences_output(self):
        """The configured replay rate must change the virtual timing (so it is a
        real knob), while the record payloads stay byte-identical."""
        p = os.path.join(CORPUS, "esp32_050hz_occupied-still.ecf1")
        slow = csi_replay.replay_file(p, rate_hz=10).summary()
        fast = csi_replay.replay_file(p, rate_hz=100).summary()
        self.assertNotEqual(slow["digest"], fast["digest"],
                            "replay rate did not affect the trace")
        self.assertGreater(slow["virtual_duration_us"], fast["virtual_duration_us"],
                           "lower rate must stretch the virtual timeline")
        # payloads identical: a CountingSink over both yields the same type tally
        self.assertEqual(slow["by_type"], fast["by_type"])

    def test_truncated_corpus_fails_loud(self):
        """A truncated capture must raise, never silently replay partial data."""
        p = os.path.join(CORPUS, "esp32_030hz_occupied-still.ecf1")
        with open(p, "rb") as f:
            buf = f.read()
        with self.assertRaises(ValueError):
            list(csi_replay.iter_records(buf[:-3]))


class TestGoldenRegression(unittest.TestCase):
    """The load-bearing CI gate: replay every corpus file at its native rate and
    assert the digest matches the checked-in golden. Any change to the format,
    the replay logic, or the corpus bytes trips this. NOTE: this guards replay
    determinism over SYNTHETIC data -- it is NOT a positioning-accuracy baseline.
    """

    def test_golden_matches(self):
        self.assertTrue(os.path.exists(GOLDEN),
                        "golden_digests.json missing -- cut it with: python3 csi_replay.py golden")
        with open(GOLDEN) as f:
            golden = json.load(f)
        live = csi_replay.build_golden(CORPUS)
        gf, lf = golden["files"], live["files"]
        self.assertEqual(set(gf), set(lf), "corpus file set drifted from golden")
        for name in sorted(gf):
            self.assertEqual(gf[name]["digest"], lf[name]["digest"],
                             "replay digest drift for %s" % name)
            self.assertEqual(gf[name]["sha256"], lf[name]["sha256"],
                             "corpus byte drift for %s" % name)


class TestAdapterStaysWired(unittest.TestCase):
    def test_b_to_ecf1_adapter_self_test(self):
        """Keep the HE's §B -> ECF1 adapter (ESPA-172 frame v1) in CI: byte-exact
        CSI round-trip, seq-gap drop reconstruction, crc8 integrity gate."""
        ecf1_adapter._self_test()  # raises/SystemExit on any failure


if __name__ == "__main__":
    unittest.main(verbosity=2)
