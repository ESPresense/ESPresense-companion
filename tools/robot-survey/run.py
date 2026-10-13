import sys, time, json, subprocess, os, math
sys.path.insert(0,'/tmp')
import ha

OUT='/tmp/survey2'
ha.init()

def pos():
    r=ha.call("ha_get_state",{"entity_id":"camera.p10_pro_ultra_map","attribute_keys":["vacuum_position"],"fields":["attributes"]})
    p=r['data']['attributes'].get('vacuum_position')
    return (p['x'],p['y'],p['a']) if p else None

def vac(keys):
    r=ha.call("ha_get_state",{"entity_id":"vacuum.p10_pro_ultra","attribute_keys":keys,"fields":["attributes","state"]})
    return r['data']

def goto(x,y):
    return ha.call("ha_call_service",{"domain":"dreame_vacuum","service":"vacuum_goto",
        "entity_id":"vacuum.p10_pro_ultra","data":{"x":x,"y":y},"wait":False})

def ensure_ready():
    for _ in range(3):
        d=vac(["status"])
        if d['state'] not in ('paused','returning','error'): return True
        ha.call("ha_call_service",{"domain":"vacuum","service":"stop","entity_id":"vacuum.p10_pro_ultra","wait":False})
        time.sleep(8)
    return False

def log(*a):
    print(*a, flush=True)

# candidate waypoints in robot mm. Ordered as an outward sweep; rejects are skipped.
WPS=[(1190,698),(3000,1500),(4500,500),(6250,2560),(8000,3500),(9500,2000),
     (6000,4500),(4000,4000),(1792,5008),(2000,7000),(0,4000),(-1500,2500),
     (-3000,1500),(-1000,0),(554,-497),(2500,-1500),(5000,-1000),(3000,-3000)]

DWELL=90
results=[]
start=time.time()

for i,(tx,ty) in enumerate(WPS):
    label=f"p{i:02d}_{tx}_{ty}"
    ensure_ready()
    p0=pos()
    log(f"\n=== {label} target=({tx},{ty}) from {p0} t={time.time()-start:.0f}s")
    try:
        goto(tx,ty)
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
    if v['attributes'].get('cleaned_area',0)>0:
        log("!!! cleaned_area > 0 -- ABORT"); break
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
                    "drift_mm":drift,"msgs":n,"capture":cap})
    json.dump(results,open(f"{OUT}/tour.json","w"),indent=1)

json.dump(results,open(f"{OUT}/tour.json","w"),indent=1)
log(f"\nDONE {len([r for r in results if r['status']=='ok'])} points in {(time.time()-start)/60:.1f} min")
