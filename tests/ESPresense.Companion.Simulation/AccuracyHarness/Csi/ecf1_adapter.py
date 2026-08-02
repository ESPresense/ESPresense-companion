#!/usr/bin/env python3
"""ECF1 <- §B adapter: ingest the Hardware Engineer's on-wire CSI frame (ESPA-172
comment §B, "binary CSI frame layout v1", magic 0xC5) and pack it into the ECF1
fixture container (ESPA-173 csi-fixture-format doc) the QA replay harness reads.

WHY THIS EXISTS
  ECF1 ended with an explicit open question to the HE: "if the native esp-csi
  binary struct is easier to dump raw, tell me its exact layout and I'll write
  the ECF1 adapter on the QA side instead." The HE answered with §B. This is that
  adapter. The bench dumps §B frames; QA converts to ECF1 off-hardware,
  deterministically -- no wasted corpus, no Python<->C# logic drift (Python owns
  bytes only; C# AccuracyHarness owns metrics/golden, same as ecf1.py).

SCOPE BOUNDARY (the substantive catch):
  §B is CSI-ONLY. It carries no BLE adverts, no heap/jitter, no window counts --
  i.e. NONE of the headline pass-criterion inputs (BLE duty loss %, advert
  miss-rate, TTFD, heap, jitter). Those live in the HE's §A per-bucket metrics
  stream (MQTT csi/bench/metrics). So a foldable corpus needs BOTH streams per
  (board x rate x scene): §B raw CSI frames -> ECF1 CSI records, AND the §A bucket
  counters -> ECF1 STAT record. This adapter takes both. The §A schema this needs
  is BUCKET_METRICS_FIELDS below -- HE: emit these per bucket or flag a deviation.
"""
import struct, sys
import ecf1  # reference container codec (same dir)

# ---- §B on-wire CSI frame v1 (HE, ESPA-172 §B) -----------------------------
B_MAGIC = 0xC5
B_VER = 1
# 24-byte fixed header, little-endian, then csi_len payload bytes, then crc8.
B_HDR = "<BBBBIQbBBBHH"           # magic,ver,board,cfg_rate,seq,ts_us,rssi,ch,bw,sig_mode,n_sub,csi_len
B_HDR_LEN = struct.calcsize(B_HDR)  # == 24
assert B_HDR_LEN == 24, B_HDR_LEN

# CRC-8/SMBUS (poly 0x07, init 0x00, no reflection, xorout 0x00) over header+payload.
# NOTE -> HE: pin this polynomial on the firmware side, or tell me yours. Mismatch
# here only costs an integrity check (CSI bytes still decode), but we should agree.
def crc8(data, poly=0x07):
    c = 0
    for byte in data:
        c ^= byte
        for _ in range(8):
            c = ((c << 1) ^ poly) & 0xff if (c & 0x80) else ((c << 1) & 0xff)
    return c

def pack_b_frame(board_id, cfg_rate_hz, seq, ts_us, rssi, channel, bw, sig_mode, csi_iq):
    n_sub = len(csi_iq) // 2          # I/Q interleaved -> 2 int8 per subcarrier
    csi_len = len(csi_iq)
    hdr = struct.pack(B_HDR, B_MAGIC, B_VER, board_id, cfg_rate_hz, seq, ts_us,
                      rssi, channel, bw, sig_mode, n_sub, csi_len)
    payload = struct.pack("<%db" % csi_len, *csi_iq)
    return hdr + payload + struct.pack("<B", crc8(hdr + payload))

def parse_b_frame(buf, off=0):
    """Parse one §B frame at buf[off:]. Returns (dict, next_off). Raises on
    bad magic/ver/crc/truncation -- a corrupt frame must NOT silently enter a fixture."""
    if buf[off] != B_MAGIC:
        raise ValueError("bad §B magic 0x%02x @%d" % (buf[off], off))
    (magic, ver, board, cfg_rate, seq, ts_us, rssi, ch, bw, sig_mode, n_sub, csi_len) = \
        struct.unpack(B_HDR, buf[off:off + B_HDR_LEN])
    if ver != B_VER:
        raise ValueError("§B ver %d unsupported" % ver)
    p0 = off + B_HDR_LEN
    payload = buf[p0:p0 + csi_len]
    if len(payload) != csi_len:
        raise ValueError("§B truncated CSI payload @%d" % off)
    crc = buf[p0 + csi_len]
    want = crc8(buf[off:p0 + csi_len])
    if crc != want:
        raise ValueError("§B crc8 mismatch @%d got 0x%02x want 0x%02x" % (off, crc, want))
    if csi_len != 2 * n_sub:
        raise ValueError("§B csi_len %d != 2*n_sub %d" % (csi_len, 2 * n_sub))
    iq = list(struct.unpack("<%db" % csi_len, payload))
    return dict(board_id=board, cfg_rate_hz=cfg_rate, seq=seq, ts_us=ts_us, rssi=rssi,
                channel=ch, bw=bw, sig_mode=sig_mode, n_sub=n_sub, csi=iq), p0 + csi_len + 1

# §A per-bucket metrics record the firmware must also emit (MQTT csi/bench/metrics).
# Maps 1:1 onto ECF1 STAT. Without it there is no knee -- §B alone can't produce it.
BUCKET_METRICS_FIELDS = (
    "free_heap", "min_free_heap", "jitter_p95_us",
    "ble_windows_attempted", "ble_windows_completed", "ble_adverts_seen",
    "csi_records_expected", "csi_records_captured", "csi_records_dropped",
    "epoch_unix_ms",
)

def b_frames_to_ecf1(path, frames_bytes, *, scene, ground_truth_occupants,
                     metrics, scan_window_ms=30, scan_interval_ms=100,
                     ref_adv_interval_ms=100, firmware_sha="unknown",
                     mac_salt=0xA5A5A5A5, ble_adverts=None):
    """Convert one bucket's §B frame stream + §A metrics into one ECF1 file.

    frames_bytes : raw concatenated §B frames for ONE (board x rate x scene) bucket.
    metrics      : dict with BUCKET_METRICS_FIELDS (the §A counters for this bucket).
    ble_adverts  : optional list of (mac_bytes, rssi, is_reference, flags) detections.
    Returns a summary dict including reconstructed seq-gap drop for cross-checking
    against metrics['csi_records_dropped'].
    """
    for f in BUCKET_METRICS_FIELDS:
        if f not in metrics:
            raise ValueError("§A metrics missing required field %r" % f)
    # ---- parse all §B frames, enforcing single-bucket invariants ----
    frames, off = [], 0
    while off < len(frames_bytes):
        fr, off = parse_b_frame(frames_bytes, off)
        frames.append(fr)
    if not frames:
        raise ValueError("empty §B stream")
    board_id = frames[0]["board_id"]; rate_hz = frames[0]["cfg_rate_hz"]
    channel = frames[0]["channel"]; bw = frames[0]["bw"]; n_sub = frames[0]["n_sub"]
    for fr in frames:
        if (fr["board_id"], fr["cfg_rate_hz"], fr["channel"], fr["bw"], fr["n_sub"]) != \
           (board_id, rate_hz, channel, bw, n_sub):
            raise ValueError("§B frame fields drift mid-bucket (capture bug): %r" % fr)
    # ---- reconstruct CSI drop from seq gaps (HE's stated loss semantics) ----
    seqs = [fr["seq"] for fr in frames]
    seq_span = seqs[-1] - seqs[0] + 1
    seq_gap_drop = seq_span - len(frames)   # missing seq numbers = dropped frames
    # ---- ts: §B ts_us is absolute u64; ECF1 record ts is u32 µs from bucket start
    t0 = frames[0]["ts_us"]
    body = bytearray()
    for fr in frames:
        rel = (fr["ts_us"] - t0) & 0xffffffff
        # §B carries no phy_rate / noise_floor -> documented UNKNOWN sentinels (unused by metrics)
        body += ecf1.rec(ecf1.REC_CSI, rel,
                         ecf1.csi_payload(fr["rssi"], 0, fr["sig_mode"], -128, fr["csi"]))
    for (mac, rssi, is_ref, flags) in (ble_adverts or []):
        body += ecf1.rec(ecf1.REC_BLE, 0, ecf1.ble_payload(ecf1.mac_hash(mac, mac_salt), rssi, is_ref, flags))
    body += ecf1.rec(ecf1.REC_STAT, (frames[-1]["ts_us"] - t0) & 0xffffffff,
                     ecf1.stat_payload(*(metrics[f] for f in BUCKET_METRICS_FIELDS)))
    hdr = ecf1.pack_header(board_id, bw, n_sub, scene, rate_hz, scan_window_ms,
                           scan_interval_ms, ref_adv_interval_ms, channel,
                           ground_truth_occupants, firmware_sha, metrics["epoch_unix_ms"], mac_salt)
    with open(path, "wb") as fh:
        fh.write(hdr + body)
    return dict(frames=len(frames), seq_gap_drop=seq_gap_drop,
                metrics_drop=metrics["csi_records_dropped"], path=path)


# ----------------------------------------------------------------------------
# SELF-TEST: synthesize §B frames with KNOWN I/Q, inject a known seq gap, convert
# to ECF1, parse the ECF1 back, and assert byte-exact CSI preservation + that the
# seq-gap drop reconstruction matches what we injected. Pure function of inputs.
# ----------------------------------------------------------------------------
def _self_test():
    import os, tempfile
    n_sub = 64
    # build 10 captured frames out of 12 expected (seq 0..11, drop seq 4 and 9)
    kept_seqs = [s for s in range(12) if s not in (4, 9)]
    known = {}   # seq -> iq list, so we can verify byte-exactness after the round-trip
    stream = bytearray()
    for i, seq in enumerate(kept_seqs):
        iq = [((seq * 7 + j) % 251) - 125 for j in range(2 * n_sub)]  # deterministic, full i8 range
        known[seq] = iq
        stream += pack_b_frame(board_id=1, cfg_rate_hz=30, seq=seq,
                               ts_us=1_000_000 + seq * 33_333, rssi=-58 - (seq % 5),
                               channel=6, bw=0, sig_mode=1, csi_iq=iq)
    # corrupt one byte and confirm crc rejects it (integrity gate works)
    bad = bytearray(stream); bad[30] ^= 0xFF
    try:
        parse_b_frame(bytes(bad), 0); raise SystemExit("FAIL: crc did not reject corruption")
    except ValueError as e:
        assert "crc8" in str(e) or "magic" in str(e), e

    metrics = dict(free_heap=163500, min_free_heap=157500, jitter_p95_us=410,
                   ble_windows_attempted=80, ble_windows_completed=80, ble_adverts_seen=80,
                   csi_records_expected=12, csi_records_captured=10, csi_records_dropped=2,
                   epoch_unix_ms=1781000000000)
    _td = tempfile.mkdtemp(prefix="ecf1-adapter-")
    out = os.path.join(_td, "_adapter_test.ecf1")
    summ = b_frames_to_ecf1(out, bytes(stream), scene=1, ground_truth_occupants=1,
                            metrics=metrics, firmware_sha="abc1234",
                            ble_adverts=[(b"\x00\x11\x22\x33\x44\x55", -70, 1, 0)])
    # ---- verify with the INDEPENDENT ecf1 container validator ----
    h, counts, stat = ecf1.validate(out)
    assert counts[ecf1.REC_CSI] == 10, counts
    assert counts[ecf1.REC_BLE] == 1, counts
    assert counts[ecf1.REC_STAT] == 1, counts
    assert h["board_id"] == 1 and h["csi_rate_hz"] == 30 and h["n_subcarriers"] == 64, h
    assert h["csi_payload_bytes"] == 128, h
    # seq-gap reconstruction must agree with the metrics drop count (2)
    assert summ["seq_gap_drop"] == 2 == summ["metrics_drop"], summ
    # ---- byte-exact CSI: re-parse ECF1 CSI records, compare I/Q to source frames ----
    b = open(out, "rb").read(); off = h["header_len"]; got = []
    while off < len(b):
        rt, plen, ts = struct.unpack("<BHI", b[off:off+7]); off += 7
        pl = b[off:off+plen]; off += plen
        if rt == ecf1.REC_CSI:
            iq = list(struct.unpack("<%db" % (plen-4), pl[4:]))
            got.append(iq)
    for idx, seq in enumerate(kept_seqs):
        assert got[idx] == known[seq], "CSI I/Q corrupted in round-trip at seq %d" % seq
    os.remove(out)
    print("PASS: §B->ECF1 adapter")
    print("  - 10/12 frames (seq 4,9 dropped) -> 10 CSI + 1 BLE + 1 STAT records")
    print("  - seq-gap drop reconstruction = %d  (matches §A metrics drop = %d)" %
          (summ["seq_gap_drop"], summ["metrics_drop"]))
    print("  - all 10 CSI I/Q payloads byte-identical to source §B frames")
    print("  - crc8 integrity gate rejects a single-bit corruption")
    print("  - ECF1 file validates under the independent ecf1.validate() container parser")

if __name__ == "__main__":
    _self_test()
