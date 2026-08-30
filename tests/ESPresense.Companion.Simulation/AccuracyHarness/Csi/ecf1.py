#!/usr/bin/env python3
"""ECF1 (ESPresense CSI Fixture v1) reference codec + synthetic generator + validator.

Reference / oracle ONLY. It defines the binary contract and emits well-formed
fixtures + a structural validator. It does NOT compute the pass-criterion metrics
or the golden baseline -- that is the C# AccuracyHarness `csi-replay` mode's job,
to avoid Python<->C# logic drift (same lesson as docs/accuracy.md). Python here
only owns the bytes; C# owns the truth.
"""
import struct, os, sys, hashlib, glob

MAGIC=b"ECF1"; HEADER_LEN=64
BOARDS={0:"esp32",1:"esp32-s3",2:"esp32-c3",3:"esp32-c6"}
SCENES={0:"vacant",1:"occupied-still",2:"occupied-moving",3:"transition"}
REC_CSI,REC_BLE,REC_STAT,REC_MARKER=1,2,3,4

def pack_header(board_id,bandwidth,n_sc,scene,rate_hz,scan_window_ms,scan_interval_ms,
                ref_adv_ms,channel,gt_occ,fw_sha,capture_unix_ms,mac_salt):
    csi_payload_bytes=2*n_sc
    h=struct.pack("<4sHHBBBBHHHHHBB8sQI",
        MAGIC,1,HEADER_LEN,board_id,bandwidth,n_sc,scene,rate_hz,
        scan_window_ms,scan_interval_ms,ref_adv_ms,csi_payload_bytes,
        channel,gt_occ,fw_sha.encode()[:8].ljust(8,b'\0'),capture_unix_ms,mac_salt)
    return h.ljust(HEADER_LEN,b'\0')

def parse_header(b):
    if b[:4]!=MAGIC: raise ValueError("bad magic %r"%b[:4])
    (magic,ver,hlen,board,bw,nsc,scene,rate,sw,si,refadv,csibytes,ch,gt,fw,cap,salt)=\
        struct.unpack("<4sHHBBBBHHHHHBB8sQI",b[:44])
    return dict(version=ver,header_len=hlen,board_id=board,bandwidth=bw,n_subcarriers=nsc,
        scene=scene,csi_rate_hz=rate,ble_scan_window_ms=sw,ble_scan_interval_ms=si,
        ref_adv_interval_ms=refadv,csi_payload_bytes=csibytes,channel=ch,
        ground_truth_occupants=gt,firmware_sha=fw.rstrip(b'\0').decode(),
        capture_unix_ms=cap,mac_salt=salt)

def rec(rec_type,ts_us,payload):
    return struct.pack("<BHI",rec_type,len(payload),ts_us)+payload

def csi_payload(rssi,phy_rate,sig_mode,noise,csi_iq):  # csi_iq: list[int8]
    return struct.pack("<bBBb",rssi,phy_rate,sig_mode,noise)+struct.pack("<%db"%len(csi_iq),*csi_iq)

def ble_payload(mac_hash,rssi,is_ref,flags):
    return struct.pack("<IbBB",mac_hash,rssi,is_ref,flags)

def stat_payload(free_heap,min_heap,jitter,att,comp,adv,exp,cap,drop,epoch_ms):
    return struct.pack("<9IQ",free_heap,min_heap,jitter,att,comp,adv,exp,cap,drop,epoch_ms)

def mac_hash(mac_bytes,salt):
    import zlib
    return zlib.crc32(bytes(a^((salt>>(8*(i%4)))&0xff) for i,a in enumerate(mac_bytes)))&0xffffffff

# ---- deterministic synthetic generator (seeded, no RNG module: LCG on a seed) ----
class LCG:
    def __init__(s,seed): s.x=seed&0xffffffff
    def next(s): s.x=(1103515245*s.x+12345)&0x7fffffff; return s.x
    def i8(s,spread): return ((s.next()% (2*spread+1))-spread)

def gen_fixture(path,board_id,rate_hz,scene,duration_s=8,n_sc=64,seed=20260609):
    """Synthetic but contract-faithful. Encodes a plausible knee:
    single-core esp32 starves BLE earlier than dual-core S3; occupied scenes
    carry higher CSI amplitude variance than vacant. Counts live in STAT so the
    C# metrics are a pure function of the bytes."""
    rng=LCG(seed ^ (board_id<<24) ^ (rate_hz<<8) ^ scene)
    scan_interval=100; scan_window=30; ref_adv=100; ch=6; salt=0xA5A5A5A5
    gt=0 if scene==0 else 1
    fw="abc1234"
    body=bytearray()
    # BLE windows over the run
    windows_attempted=duration_s*1000//scan_interval
    # duty loss model: knee ~30Hz on S3, ~20Hz on single-core esp32
    knee = 30 if board_id==1 else 20
    over = max(0, rate_hz-knee)
    duty_loss = min(0.85, (over/100.0)*(1.6 if board_id==0 else 1.0))  # fraction
    windows_completed=int(round(windows_attempted*(1-duty_loss)))
    csi_expected = rate_hz*duration_s
    csi_drop = int(round(csi_expected * min(0.9, (over/120.0)) )) if rate_hz>0 else 0
    csi_captured = csi_expected - csi_drop
    adverts_expected = duration_s*1000//ref_adv
    adverts_seen = int(round(adverts_expected*(windows_completed/max(1,windows_attempted))))
    free_heap=180000 - rate_hz*350 - (40000 if board_id==0 else 0)
    min_heap=free_heap-6000
    jitter=200 + rate_hz*(18 if board_id==0 else 7)
    # emit a handful of CSI + BLE records + one STAT (counts are the source of truth)
    amp_spread = 6 if scene==0 else 18  # occupied => more variance
    ts=0
    for k in range(min(csi_captured, 12)):
        ts+= 1000000//max(1,rate_hz) if rate_hz else 50000
        iq=[rng.i8(amp_spread) for _ in range(2*n_sc)]
        body+=rec(REC_CSI,ts & 0xffffffff, csi_payload(rng.i8(40)-60,11,1,-92,iq))
    refmac=mac_hash(b'\x00\x11\x22\x33\x44\x55',salt)
    for k in range(min(adverts_seen,6)):
        body+=rec(REC_BLE, (ts+k*1000)&0xffffffff, ble_payload(refmac, rng.i8(30)-70,1,0))
    body+=rec(REC_STAT, ts&0xffffffff, stat_payload(free_heap,min_heap,jitter,
        windows_attempted,windows_completed,adverts_seen,csi_expected,csi_captured,csi_drop,
        1781000000000))
    hdr=pack_header(board_id,0,n_sc,scene,rate_hz,scan_window,scan_interval,ref_adv,ch,gt,fw,
        1781000000000,salt)
    with open(path,"wb") as f: f.write(hdr+body)
    return dict(windows_attempted=windows_attempted,windows_completed=windows_completed,
        csi_expected=csi_expected,csi_captured=csi_captured,csi_drop=csi_drop,
        adverts_expected=adverts_expected,adverts_seen=adverts_seen,
        min_heap=min_heap,jitter=jitter)

def validate(path):
    b=open(path,"rb").read()
    if len(b)<HEADER_LEN: raise ValueError("short file")
    h=parse_header(b); off=h["header_len"]; counts={1:0,2:0,3:0,4:0}; stat=None
    expect_csi_bytes=h["csi_payload_bytes"]
    while off < len(b):
        rt,plen,ts=struct.unpack("<BHI",b[off:off+7]); off+=7
        pl=b[off:off+plen]; off+=plen
        if len(pl)!=plen: raise ValueError("truncated record")
        counts[rt]=counts.get(rt,0)+1
        if rt==REC_CSI:
            got=len(pl)-4
            if got!=expect_csi_bytes: raise ValueError("CSI payload %d != header %d"%(got,expect_csi_bytes))
        if rt==REC_STAT:
            stat=struct.unpack("<9IQ",pl)
    return h,counts,stat
