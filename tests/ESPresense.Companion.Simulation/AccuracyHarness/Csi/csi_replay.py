#!/usr/bin/env python3
"""Deterministic ECF1 CSI replay fixture (QA-1 / ESPA-173).

Reads ECF1 capture files (the binary container defined by the ESPA-173
``csi-fixture-format`` doc and ``ecf1.py``) and re-feeds their records into a
``CsiSink`` seam at a configured replay rate, in *virtual* time. The replay is a
pure function of ``(file bytes, replay rate)``:

    same (bytes, rate)  ->  byte-identical event trace  ->  identical SHA-256 digest

with no wall-clock dependence. Pacing controls *when* an event is delivered; the
digest captures *what* is delivered. ``--realtime`` inserts real sleeps so a
soak/interactive run plays at human speed, but it does NOT change the output
digest -- the test suite asserts that.

This is the off-hardware regression substrate the bench corpus feeds: coexistence
/ occupancy ingestion logic can be replayed deterministically in CI with no board
on the bench, in the same spirit as the simulated trilateration harness (ESPA-21).

DIVISION OF LABOR (do not relitigate -- same rule as docs/accuracy.md):
  * Python owns the BYTES and the REPLAY DETERMINISM. ``golden_digests.json``
    locks the replayed event trace and is a legitimate regression guard over the
    SYNTHETIC corpus -- it guards that the format + replay are byte-stable.
  * The C# AccuracyHarness owns the occupancy/coexistence METRICS and the
    *accuracy* golden. That golden must be cut from a REAL captured corpus, not
    synthetic data -- see README.md "Status: synthetic interim corpus".
"""
import glob
import hashlib
import json
import os
import struct
import sys
import time

import ecf1  # verified reference container codec (same dir)

# ECF1 per-record header: type:u8, payload_len:u16, ts_us:u32  (see ecf1.rec)
REC_HDR = "<BHI"
REC_HDR_LEN = struct.calcsize(REC_HDR)  # == 7
assert REC_HDR_LEN == 7, REC_HDR_LEN

REC_NAME = {ecf1.REC_CSI: "csi", ecf1.REC_BLE: "ble",
            ecf1.REC_STAT: "stat", ecf1.REC_MARKER: "marker"}


def _read(path):
    with open(path, "rb") as f:
        return f.read()


def iter_records(buf):
    """Yield ``(rec_type, capture_ts_us, payload)`` for every record in an ECF1
    buffer, in capture order. Raises on a truncated record -- a malformed corpus
    must fail loudly, never replay partial garbage."""
    h = ecf1.parse_header(buf)
    off = h["header_len"]
    n = len(buf)
    while off < n:
        if off + REC_HDR_LEN > n:
            raise ValueError("truncated record header @%d" % off)
        rt, plen, ts = struct.unpack(REC_HDR, buf[off:off + REC_HDR_LEN])
        off += REC_HDR_LEN
        pl = buf[off:off + plen]
        off += plen
        if len(pl) != plen:
            raise ValueError("truncated record payload @%d (want %d)" % (off, plen))
        yield rt, ts, pl


class CsiSink:
    """The replay seam. The companion's real CSI ingestion path subclasses this
    and overrides :meth:`on_event`; the stub sinks below stand in for it so the
    replay is provably deterministic without a board on the bench. Keeping a
    single seam means the day a real ingestor lands, it drops in here unchanged."""

    def on_event(self, virtual_ts_us, rec_type, capture_ts_us, payload):
        raise NotImplementedError

    def summary(self):
        return {}


class CountingSink(CsiSink):
    """Tallies delivered records by type and tracks the virtual timeline."""

    def __init__(self):
        self.counts = {t: 0 for t in REC_NAME}
        self.delivered = 0
        self.virtual_duration_us = 0

    def on_event(self, virtual_ts_us, rec_type, capture_ts_us, payload):
        self.counts[rec_type] = self.counts.get(rec_type, 0) + 1
        self.delivered += 1
        if virtual_ts_us > self.virtual_duration_us:
            self.virtual_duration_us = virtual_ts_us

    def summary(self):
        return {"delivered": self.delivered,
                "virtual_duration_us": self.virtual_duration_us,
                "by_type": {REC_NAME[t]: c for t, c in self.counts.items()}}


class DigestSink(CountingSink):
    """Folds a canonical, length-prefixed, big-endian serialization of each
    delivered event into a streaming SHA-256. Order- and content-sensitive by
    construction, so any change to the bytes, the record order, or the virtual
    timing moves the digest -- which is exactly what the CI regression guard
    wants."""

    def __init__(self):
        super().__init__()
        self._h = hashlib.sha256()

    def on_event(self, virtual_ts_us, rec_type, capture_ts_us, payload):
        super().on_event(virtual_ts_us, rec_type, capture_ts_us, payload)
        rec = struct.pack(">QBI", virtual_ts_us & 0xFFFFFFFFFFFFFFFF,
                          rec_type & 0xFF, capture_ts_us & 0xFFFFFFFF) + payload
        self._h.update(struct.pack(">I", len(rec)))
        self._h.update(rec)

    def summary(self):
        s = super().summary()
        s["digest"] = self._h.hexdigest()
        return s


def replay_buffer(buf, rate_hz, sink=None, realtime=False, time_scale=1.0):
    """Replay one ECF1 buffer into ``sink`` at ``rate_hz``.

    The replay rate is the CSI cadence: consecutive CSI records are delivered one
    replay period (``1e6 / rate`` us) apart on the virtual clock. Non-CSI records
    (BLE / STAT / MARKER) ride along at the current virtual time, preserving their
    position in the captured stream. ``rate_hz <= 0`` (e.g. the 0 Hz baseline
    buckets, which carry no CSI) delivers every record at virtual t=0 in order.

    ``realtime`` inserts real sleeps for the virtual gaps (scaled by
    ``time_scale``) for soak runs; it never changes what the sink sees.
    """
    sink = DigestSink() if sink is None else sink
    period_us = (1_000_000 // rate_hz) if rate_hz and rate_hz > 0 else 0
    t_v = 0
    prev_v = 0
    for rt, cts, pl in iter_records(buf):
        if realtime:
            gap = t_v - prev_v
            if gap > 0:
                time.sleep(gap / 1e6 * time_scale)
        sink.on_event(t_v, rt, cts, pl)
        prev_v = t_v
        if rt == ecf1.REC_CSI:
            t_v += period_us
    return sink


def replay_file(path, rate_hz=None, sink=None, realtime=False, time_scale=1.0):
    """Replay an ECF1 file. ``rate_hz=None`` uses the capture's own header rate
    (replay at native cadence); 0 Hz buckets fall back to virtual t=0 delivery."""
    buf = _read(path)
    if rate_hz is None:
        rate_hz = ecf1.parse_header(buf)["csi_rate_hz"]
    return replay_buffer(buf, rate_hz, sink=sink, realtime=realtime, time_scale=time_scale)


def file_record(path, rate_hz=None):
    """One golden/manifest row for a corpus file: header summary + replay digest."""
    buf = _read(path)
    h = ecf1.parse_header(buf)
    eff_rate = h["csi_rate_hz"] if rate_hz is None else rate_hz
    s = replay_buffer(buf, eff_rate).summary()
    return {
        "board": ecf1.BOARDS.get(h["board_id"], "?%d" % h["board_id"]),
        "scene": ecf1.SCENES.get(h["scene"], "?%d" % h["scene"]),
        "capture_rate_hz": h["csi_rate_hz"],
        "replay_rate_hz": eff_rate,
        "n_subcarriers": h["n_subcarriers"],
        "bytes": len(buf),
        "sha256": hashlib.sha256(buf).hexdigest(),
        "by_type": s["by_type"],
        "virtual_duration_us": s["virtual_duration_us"],
        "digest": s["digest"],
    }


def corpus_files(corpus_dir):
    return sorted(glob.glob(os.path.join(corpus_dir, "*.ecf1")))


def build_golden(corpus_dir):
    """Deterministic golden map: each file replayed at its native capture rate."""
    golden = {"_note": "SYNTHETIC corpus. Guards ECF1 replay determinism + byte "
                       "stability, NOT positioning/occupancy accuracy. Re-cut the "
                       "accuracy golden in the C# AccuracyHarness from a REAL "
                       "captured corpus (prototype FW-1 milestone-1).",
              "files": {}}
    for p in corpus_files(corpus_dir):
        golden["files"][os.path.basename(p)] = file_record(p, rate_hz=None)
    return golden


# --------------------------------------------------------------------------- CLI
def _cmd_replay(args):
    rate = None
    realtime = False
    as_json = False
    paths = []
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--rate":
            rate = int(args[i + 1]); i += 2; continue
        if a == "--realtime":
            realtime = True; i += 1; continue
        if a == "--json":
            as_json = True; i += 1; continue
        paths.append(a); i += 1
    if not paths:
        print("usage: csi_replay.py replay <file.ecf1|dir> [--rate HZ] [--realtime] [--json]")
        return 2
    target = paths[0]
    files = corpus_files(target) if os.path.isdir(target) else [target]
    if not files:
        print("no .ecf1 files under %s" % target); return 2
    agg = hashlib.sha256()
    rows = {}
    for p in files:
        s = replay_file(p, rate_hz=rate, realtime=realtime).summary()
        rows[os.path.basename(p)] = s
        agg.update(s["digest"].encode())
    out = {"replay_rate_hz": ("native" if rate is None else rate),
           "files": rows, "aggregate_digest": agg.hexdigest()}
    if as_json:
        print(json.dumps(out, indent=2, sort_keys=True))
    else:
        for name, s in rows.items():
            bt = s["by_type"]
            print("%-34s csi=%-3d ble=%-2d stat=%-2d  vdur=%-8d  %s"
                  % (name, bt["csi"], bt["ble"], bt["stat"],
                     s["virtual_duration_us"], s["digest"][:16]))
        print("aggregate %s  (rate=%s, %d files)"
              % (agg.hexdigest(), out["replay_rate_hz"], len(files)))
    return 0


def _cmd_manifest(args):
    target = args[0] if args else os.path.join(os.path.dirname(__file__), "corpus")
    files = corpus_files(target) if os.path.isdir(target) else [target]
    out = {os.path.basename(p): file_record(p) for p in files}
    print(json.dumps(out, indent=2, sort_keys=True))
    return 0


def _cmd_golden(args):
    corpus_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "corpus")
    out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "golden_digests.json")
    i = 0
    while i < len(args):
        if args[i] == "--corpus":
            corpus_dir = args[i + 1]; i += 2; continue
        if args[i] == "--out":
            out_path = args[i + 1]; i += 2; continue
        i += 1
    golden = build_golden(corpus_dir)
    with open(out_path, "w") as f:
        json.dump(golden, f, indent=2, sort_keys=True)
        f.write("\n")
    print("wrote %d golden digests -> %s" % (len(golden["files"]), out_path))
    return 0


def main(argv):
    if not argv:
        print("usage: csi_replay.py {replay|manifest|golden} ...")
        return 2
    cmd, rest = argv[0], argv[1:]
    if cmd == "replay":
        return _cmd_replay(rest)
    if cmd == "manifest":
        return _cmd_manifest(rest)
    if cmd == "golden":
        return _cmd_golden(rest)
    print("unknown command %r" % cmd)
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
