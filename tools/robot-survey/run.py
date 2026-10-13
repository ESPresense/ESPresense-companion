import sys, time, json, subprocess, os, math
HERE=os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0,HERE)
import ha

OUT=os.environ.get('SURVEY_OUT') or os.path.join(HERE,'captures')
ha.init()

def pos():
    r=ha.call("get_state",{"entity_id":"camera.p10_pro_ultra_map","fields":["vacuum_position"]})
    p=r['attributes'].get('vacuum_position')
    return (p['x'],p['y'],p['a']) if p else None

def vac(keys):
    return ha.call("get_state",{"entity_id":"vacuum.p10_pro_ultra","fields":keys})

def goto(x,y):
    return ha.call("call_service",{"domain":"dreame_vacuum","service":"vacuum_goto",
        "entity_id":"vacuum.p10_pro_ultra","data":{"x":x,"y":y}})

def ensure_ready():
    for _ in range(3):
        d=vac(["status"])
        if d['state'] not in ('paused','returning','error'): return True
        ha.call("call_service",{"domain":"vacuum","service":"stop","entity_id":"vacuum.p10_pro_ultra"})
        time.sleep(8)
    return False

def log(*a):
    print(*a, flush=True)

# candidate waypoints in robot mm. Ordered as an outward sweep; rejects are skipped.
WPS=[(1190,698),(3000,1500),(4500,500),(6250,2560),(8000,3500),(9500,2000),
     (6000,4500),(4000,4000),(1792,5008),(2000,7000),(0,4000),(-1500,2500),
     (-3000,1500),(-1000,0),(554,-497),(2500,-1500),(5000,-1000),(3000,-3000)]
# In-map waypoints: anchor_waypoints.json (nearest open floor to placed nodes + room centres, from
# plan_anchors.py) plus farthest-point samples of the map image >=1.2 m from those. Most of the
# hand-picked list above is off the map.
WPS+=[(-3180, -420), (-2200, -1762), (875, -862), (469, 524), (2825, -487), (3150, 3000), (4412, 2875), (3887, 5037), (2290, 6340), (3200, 8344), (7681, 7319), (9694, 8750), (7300, 125), (9800, -675), (8962, -5137), (-2037, 3925), (-4280, 5150), (-5569, 6224)]

# Living, replacing the off-map centre (-3180,-420); second is the fallback if the first is off-map too.
WPS+=[(-4450, 900), (-4650, 1100)]
# Live-frame picks from the corrected transform (anchor_waypoints.json, 2nd revision): Laundry anchor,
# Family, Dining and Laundry centres. Earlier anchor picks were in the saved-map frame; any that the
# vacuum accepts are still valid captures, since the tour records where the robot actually stopped.
WPS+=[(6397, -2817), (10455, -885), (8501, -4541), (1158, 6532)]

def fingerprint(path):
    """Mean RSSI per receiving node for one capture."""
    per={}
    for line in open(path, errors='replace'):
        parts=line.split(' ',2)
        if len(parts)<3 or not parts[1].startswith('espresense/devices/node:calibrate/'): continue
        try: per.setdefault(parts[1].rsplit('/',1)[1],[]).append(float(json.loads(parts[2])['rssi']))
        except Exception: pass
    return {k:sum(v)/len(v) for k,v in per.items() if len(v)>=3}
last_fp=None

DWELL=90
# Resume: keep what an earlier run already captured and skip those waypoints.
try: results=json.load(open(f"{OUT}/tour.json"))
except FileNotFoundError: results=[]
# Also skip waypoints the vacuum already called off-map; that verdict does not change between runs.
done={r['label'] for r in results if r['status']=='ok' or 'not inside the map' in r.get('reason','')}
start=time.time()
# Logged, not enforced: cleaning during the tour is allowed.
AREA0=vac(["cleaned_area"])['attributes'].get('cleaned_area',0)

for i,(tx,ty) in enumerate(WPS):
    label=f"p{i:02d}_{tx}_{ty}"
    if label in done: continue
    ensure_ready()
    p0=pos()
    log(f"\n=== {label} target=({tx},{ty}) from {p0} t={time.time()-start:.0f}s")
    try:
        r=goto(tx,ty)
        if r: log(f"  goto -> {str(r)[:300]}")
        if isinstance(r,str) and r.startswith("Error"):
            results.append({"label":label,"target":[tx,ty],"status":"rejected","reason":r[:200]}); continue
    except Exception as e:
        log(f"  goto error: {e}"); continue
    # wait for arrival
    arrived=False; last=p0; still=0
    t0=time.time()
    while time.time()-t0 < 180:
        time.sleep(6)
        p=pos()
        if p is None: continue
        d=math.hypot(p[0]-tx,p[1]-ty)
        moved=math.hypot(p[0]-last[0],p[1]-last[1]) if last else 999
        last=p
        if d<300:
            still+=1
            if still>=2: arrived=True; break
        elif moved<40 and time.time()-t0>40:
            still+=1
            if still>=5:
                log(f"  stalled at {p}, dist-to-target {d:.0f}mm"); break
        else:
            still=0
    v=vac(["cleaned_area","status","battery_level"])
    if v['attributes'].get('cleaned_area',0)>AREA0:
        log(f"  note: cleaned_area {v['attributes'].get('cleaned_area')} (was {AREA0} at start)")
    pa=pos()
    if p0 and pa and math.hypot(pa[0]-p0[0],pa[1]-p0[1])<100 and math.hypot(pa[0]-tx,pa[1]-ty)>400:
        log(f"  REJECTED (never moved). skipping."); 
        results.append({"label":label,"target":[tx,ty],"status":"rejected"}); continue
    log(f"  settled at {pa} arrived={arrived} status={v['attributes'].get('status')} batt={v['attributes'].get('battery_level')}")
    # capture
    cap=f"{OUT}/cap_{label}.txt"
    with open(cap,'w') as f:
        subprocess.run(["mosquitto_sub","-h","mqtt.z13.org","-p","1883","-v","-W",str(DWELL),
                        "-F","%U %t %p",
                        "-t","espresense/devices/node:calibrate/+",
                        "-t","espresense/devices/+/calibrate"],stdout=f,stderr=subprocess.DEVNULL)
    pb=pos()
    drift=math.hypot(pb[0]-pa[0],pb[1]-pa[1]) if pa and pb else None
    n=sum(1 for _ in open(cap))
    log(f"  captured {n} msgs, drift during dwell {drift:.0f}mm, end {pb}")
    results.append({"label":label,"target":[tx,ty],"status":"ok","pos_before":pa,"pos_after":pb,
                    "drift_mm":drift,"msgs":n,"capture":os.path.basename(cap)})
    json.dump(results,open(f"{OUT}/tour.json","w"),indent=1)
    # Beacon check: if the robot moved 2 m+ since the last capture and every node still hears the
    # same RSSI, the calibrate hat has come off and is lying still. Stop rather than log junk.
    fp=fingerprint(cap)
    if last_fp and pa and math.hypot(pa[0]-last_fp[0][0],pa[1]-last_fp[0][1])>=2000:
        common=set(fp)&set(last_fp[1])
        if len(common)>=4:
            rms=math.sqrt(sum((fp[k]-last_fp[1][k])**2 for k in common)/len(common))
            log(f"  fingerprint change {rms:.1f} dB over {math.hypot(pa[0]-last_fp[0][0],pa[1]-last_fp[0][1])/1000:.1f} m")
            if rms<2.0:
                log("!!! RSSI fingerprint unchanged after moving 2 m+: the beacon is off the robot -- ABORT"); break
    last_fp=(pa,fp)

json.dump(results,open(f"{OUT}/tour.json","w"),indent=1)
log(f"\nDONE {len([r for r in results if r['status']=='ok'])} points in {(time.time()-start)/60:.1f} min")
