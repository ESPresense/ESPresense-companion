"""Propose tour waypoints (robot-frame mm) directly beneath placed nodes, for frame anchoring.

Uses the geometry-only frames from fit_frame.py (frames.json). Each node's floorplan x,y is taken
into the LIVE robot frame (the one vacuum_goto accepts); if that spot is not open floor in the live
map, with CLEAR metres of free
space all round, the nearest spot that is (within MAX_SHIFT) is used and the offset reported.
Spots within MIN_GAP of an already-captured position are dropped.
"""
import json, math, os, re, sys
from pngio import SavedMap
from frames import load

HERE=os.path.dirname(os.path.abspath(__file__))
CACHE=os.environ.get('FIT_CACHE','/tmp/fitframe')
CLEAR,MAX_SHIFT,MIN_GAP=0.4,1.2,1.5
F=load()
def to_robot(fx,fy):  # floor metres -> live-frame mm
    x,y=F['floor_to_live'](fx,fy); return round(x*1000), round(y*1000)

m=SavedMap(os.path.join(HERE,'maps/live.png'), json.load(open(os.path.join(HERE,'maps/live.json')))['calibration_points'])
def clear(X,Y):
    return all(m.free(X+CLEAR*1000*math.cos(a)*r, Y+CLEAR*1000*math.sin(a)*r)
               for r in (0,0.5,1) for a in [i*math.pi/8 for i in range(16)])

cfg=open(f'{CACHE}/config.yaml').read()
blk=re.search(r'^nodes:\s*$(.*?)(?=^\S|\Z)',cfg,re.M|re.S).group(1)
nodes=[(n.group(1).strip(),*[float(v) for v in n.group(2).split(',')])
       for n in re.finditer(r'-\s+name:\s*(.+?)\n\s+point:\s*\[([^\]]+)\]',blk)]

tour=json.load(open(os.path.join(HERE,'captures/tour.json')))
done=[(t['pos_before'][0],t['pos_before'][1]) for t in tour if t['status']=='ok']

def settle(X0,Y0):
    for r in [i*0.1 for i in range(int(MAX_SHIFT*10)+1)]:
        for a in ([0] if r==0 else [i*math.pi/12 for i in range(24)]):
            X,Y=X0+r*1000*math.cos(a), Y0+r*1000*math.sin(a)
            if clear(X,Y): return round(X),round(Y),r
    return None

out=[]
for name,fx,fy,fz in nodes:
    best=settle(*to_robot(fx,fy))
    if not best:
        print(f"  skip {name:<17} floor ({fx},{fy}): no open floor within {MAX_SHIFT} m", file=sys.stderr); continue
    X,Y,r=best
    gap=min(math.dist((X,Y),d) for d in done+[(o[0],o[1]) for o in out])/1000
    if gap<MIN_GAP:
        print(f"  skip {name:<17} only {gap:.1f} m from a captured or planned point", file=sys.stderr); continue
    note=f"under {name} (z {fz} m)" + (f", shifted {r:.1f} m to open floor" if r else "")
    out.append([X,Y,note]); print(f"  {X:6d},{Y:6d}  {note}")

# Fill in with room centres the anchors don't already cover, for spatial spread.
first=cfg.split('- id: second')[0].split('- id: first')[1]
for m_ in re.finditer(r'name:\s*(\w+)\s*\n\s*points:\s*\n((?:\s*- \[[^\]]+\]\s*\n)+)',first):
    pts=[tuple(float(v) for v in q.split(',')) for q in re.findall(r'\[([^\]]+)\]',m_.group(2))]
    cx,cy=sum(p[0] for p in pts)/len(pts),sum(p[1] for p in pts)/len(pts)
    best=settle(*to_robot(cx,cy))
    if not best: print(f"  skip centre of {m_.group(1)}: not open floor in the robot map", file=sys.stderr); continue
    X,Y,r=best
    if min(math.dist((X,Y),d) for d in done+[(o[0],o[1]) for o in out])/1000<MIN_GAP: continue
    out.append([X,Y,f"centre of {m_.group(1)}"]); print(f"  {X:6d},{Y:6d}  centre of {m_.group(1)}")

json.dump(out,open(os.path.join(HERE,'anchor_waypoints.json'),'w'),indent=1)
print(f"{len(out)} anchors -> anchor_waypoints.json")
