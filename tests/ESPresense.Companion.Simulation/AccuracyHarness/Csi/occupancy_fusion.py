#!/usr/bin/env python3
"""CSI+BLE room-occupancy fusion (CMP-1 / ESPA-197).

Turns raw CSI amplitude data + the companion's existing BLE room-presence
signal into a single coarse room-occupancy decision. Anchored on device-free
occupancy in the esp-csi/esp-radar sense (energy/variance in the channel
response), NOT pose, vitals, or through-wall sensing -- those stay out of
scope per the feasibility doc (ESPA-168) and the ESPA-195 kill criteria.

STATUS: design + SYNTHETIC-data pass only (ESPA-197). Real validation is
blocked on FW-1's measured corpus (ESPA-196) -- see FUSION_DESIGN.md.

SCOPE BOUNDARY (read before reusing ECF1 BLE records as a presence proxy):
  ECF1's REC_BLE records model a fixed *reference advertiser* used to measure
  BLE<->CSI coexistence duty-cycle loss (QA-1 / ESPA-173). They are NOT a
  stand-in for the companion's real per-room BLE presence signal (that comes
  from the existing trilateration pipeline, tracked devices -> nearest room).
  This module therefore validates the CSI-only motion/energy feature against
  the ECF1 corpus's ground_truth_occupants (real, checkable), and validates
  the fusion RULE against synthetic (csi_score, ble_present) truth-table
  cases (not ECF1 BLE records). Do not wire ECF1 REC_BLE into fuse() as if it
  were room presence -- that would silently miscalibrate the fusion the day
  a real ingestor lands.

Division of labor (same rule as docs/accuracy.md and the QA-1 harness):
  Python here is a research/design prototype, not production code. The
  production ingestor is C# (ESPresense.Companion), lands with a real corpus
  per FUSION_DESIGN.md's rollout plan. This module's job is to nail down the
  feature + fusion rule and prove them against data we have today.
"""
import glob
import json
import os
import struct
import sys

import ecf1

CSI_PAYLOAD_PREFIX = "<bBBb"  # rssi, phy_rate, sig_mode, noise (see ecf1.csi_payload)
CSI_PREFIX_LEN = struct.calcsize(CSI_PAYLOAD_PREFIX)
REC_HDR = "<BHI"
REC_HDR_LEN = struct.calcsize(REC_HDR)


def _iter_records(buf):
    h = ecf1.parse_header(buf)
    off = h["header_len"]
    n = len(buf)
    while off < n:
        rt, plen, ts = struct.unpack(REC_HDR, buf[off:off + REC_HDR_LEN])
        off += REC_HDR_LEN
        pl = buf[off:off + plen]
        off += plen
        yield rt, ts, pl
    return


def csi_motion_score(buf):
    """Device-free motion/energy feature from raw CSI (esp-radar style):
    mean per-subcarrier amplitude variance across the captured CSI frames in
    one bucket, normalized to a 0..1-ish scale. Higher = more channel
    modulation = more likely a body is present and moving air/RF around it.

    Returns None if the bucket carries no CSI (e.g. the 0 Hz baseline
    buckets) -- callers must treat "no CSI" as a missing feature, not a zero
    score (silently treating "no data" as "no motion" would bias toward
    false-vacant, which is the wrong failure mode for an HVAC/safety signal).
    """
    h = ecf1.parse_header(buf)
    n_sc = h["n_subcarriers"]
    frames = []  # each: list of per-subcarrier amplitude (float)
    for rt, ts, pl in _iter_records(buf):
        if rt != ecf1.REC_CSI:
            continue
        iq = pl[CSI_PREFIX_LEN:]
        amps = []
        for k in range(n_sc):
            i_val = struct.unpack_from("<b", iq, 2 * k)[0]
            q_val = struct.unpack_from("<b", iq, 2 * k + 1)[0]
            amps.append((i_val * i_val + q_val * q_val) ** 0.5)
        frames.append(amps)
    if not frames:
        return None
    n_frames = len(frames)
    # per-subcarrier variance across time, then mean across subcarriers
    means = [sum(f[k] for f in frames) / n_frames for k in range(n_sc)]
    variances = [
        sum((f[k] - means[k]) ** 2 for f in frames) / n_frames for k in range(n_sc)
    ]
    mean_var = sum(variances) / n_sc
    # normalize: sqrt to bring variance back to an amplitude-like scale,
    # divide by a fixed reference ceiling so the score is comparable across
    # boards/rates without per-node calibration (that's CMP-3's job -- this
    # is a v1 global placeholder, see FUSION_DESIGN.md "Threshold, honestly").
    REF_CEILING = 40.0
    return min(1.0, (mean_var ** 0.5) / REF_CEILING)


# v1 global placeholder threshold, picked by inspecting the synthetic
# corpus's vacant/occupied-still separation (see main()): vacant clusters
# ~0.043-0.047, occupied-still clusters ~0.117-0.131 across both boards and
# all rates, so the midpoint (0.08) gives full margin on both sides on this
# corpus. This is a SYNTHETIC-corpus number, not a real-world calibration --
# CMP-3 replaces it with a per-node adaptive baseline (see FUSION_DESIGN.md).
CSI_OCCUPIED_THRESHOLD = 0.08


def fuse(csi_score, ble_present):
    """The v1 fusion rule. Deliberately simple and inspectable -- not ML --
    because the kill criteria (ESPA-195) are about robustness and drift, and
    a rule you can explain in one line is a rule you can debug in a real
    home. Bias is toward NOT missing an occupant: either signal firing means
    "occupied". This matters for the mission (elopement/fall-response,
    HVAC-by-occupancy) where a false "vacant" is the worse failure mode.

    csi_score   : float in [0,1] from csi_motion_score(), or None if no CSI
                  reading was available this window (node offline/no data).
    ble_present : True/False/None. None = no BLE-tracked device AND no
                  reading taken (distinct from False = actively checked and
                  nothing detected). Companion's existing BLE pipeline
                  supplies this; it is never ECF1's REC_BLE reference beacon.

    Returns (occupied: bool, confidence: "high"|"medium"|"low", reason: str)
    """
    csi_hit = csi_score is not None and csi_score >= CSI_OCCUPIED_THRESHOLD
    if ble_present is True and csi_hit:
        return True, "high", "ble+csi agree occupied"
    if ble_present is True and not csi_hit:
        # tracked device present but CSI quiet (asleep, still, or CSI node
        # down) -- trust BLE, it is the more direct signal.
        return True, "medium", "ble present, csi quiet/unavailable"
    if ble_present is False and csi_hit:
        # exactly the case CSI exists for: a device-free occupant.
        return True, "medium", "no tracked device, csi indicates motion/energy"
    if ble_present is False and not csi_hit and csi_score is not None:
        return False, "high", "ble+csi agree vacant"
    if ble_present is None and csi_hit:
        return True, "low", "csi indicates occupancy, ble signal unavailable"
    if ble_present is None and csi_score is None:
        return False, "low", "no signal available -- treat as unknown/vacant with low confidence"
    return False, "medium", "ble absent, csi quiet"


def classify_file(path):
    with open(path, "rb") as f:
        buf = f.read()
    h = ecf1.parse_header(buf)
    score = csi_motion_score(buf)
    occupied, confidence, reason = fuse(score, ble_present=None)  # CSI-only pass, see module docstring
    gt = bool(h["ground_truth_occupants"])
    return dict(
        file=os.path.basename(path),
        board=ecf1.BOARDS.get(h["board_id"], "?"),
        rate_hz=h["csi_rate_hz"],
        scene=ecf1.SCENES.get(h["scene"], "?"),
        csi_score=score,
        predicted_occupied=occupied,
        confidence=confidence,
        reason=reason,
        ground_truth_occupied=gt,
        correct=(score is not None and occupied == gt),
    )


def main(argv):
    corpus_dir = argv[0] if argv else os.path.join(os.path.dirname(__file__), "corpus")
    files = sorted(glob.glob(os.path.join(corpus_dir, "*.ecf1")))
    rows = [classify_file(p) for p in files]
    scored = [r for r in rows if r["csi_score"] is not None]
    correct = sum(1 for r in scored if r["correct"])
    print(json.dumps(rows, indent=2, sort_keys=True))
    print(
        "\nCSI-only accuracy on synthetic corpus (excludes 0Hz/no-CSI buckets): "
        f"{correct}/{len(scored)} ({100.0 * correct / len(scored):.0f}%)",
        file=sys.stderr,
    )
    return 0 if correct == len(scored) else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
