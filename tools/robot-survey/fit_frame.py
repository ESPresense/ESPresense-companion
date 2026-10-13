"""Fit the robot-LiDAR -> floorplan transform from geometry only, in two rigid steps:

  live  -> saved : register the live map image (the frame vacuum_position uses today) onto the
                   saved 'Downstairs' map image by free-floor overlap. The robot lost its saved map
                   and rebuilt one rotated ~45 degrees, so these differ.
  saved -> floor : below.

Writes frames.json; frames.py turns it into conversion functions.

Inputs for the second step are the robot's own room segmentation (axis-aligned room bounding boxes
from the saved-map camera, camera.p10_pro_ultra_map_1) and the Companion's first-floor room
polygons. Every rotation in 90-degree steps, with and without a mirror, is tried; for each, the
translation that maximises the summed IoU of each robot room with its best-matching floorplan room
is searched. Hard constraints: the dock lands in the Foyer (Darrell), and some robot room matches
Family. Survey points are only checked for being indoors.

The map images and calibrations are snapshots in maps/ (the live-map camera has since vanished
from HA); the Companion config is fetched read-only from HA and cached in FIT_CACHE.

Per the README, RSSI must never produce this transform. The range residual printed at the end is
a cross-check only; it is not written anywhere and nothing reads it.
"""
import json, math, os, re, sys

HERE=os.path.dirname(os.path.abspath(__file__))
FIXTURE=os.path.join(HERE,'../../tests/ESPresense.Companion.Tests/TestData/robot-survey-2026-10-13.json')
CACHE=os.environ.get('FIT_CACHE','/tmp/fitframe')
DOCK_LIVE=(-0.274,0.329)  # charger_position, live frame, metres
DOCK_ROOM='Foyer'
TABLE_ROOM='Family'
MAPS=os.path.join(HERE,'maps')

def cached(name, fetch):
    path=os.path.join(CACHE,name)
    if not os.path.exists(path):
        os.makedirs(CACHE,exist_ok=True)
        sys.path.insert(0,HERE); import ha; ha.init()   # read-only calls only
        open(path,'w').write(fetch(ha))
    return open(path).read()

from pngio import SavedMap
from frames import rigid

# --- step 1: live -> saved, by free-floor overlap of the two map images -----------------------
saved_state=json.load(open(f'{MAPS}/saved.json'))
SAVED=SavedMap(f'{MAPS}/saved.png', saved_state['calibration_points'])
LIVE=SavedMap(f'{MAPS}/live.png', json.load(open(f'{MAPS}/live.json'))['calibration_points'])

def free_cells(m,c):
    step=max(1,int(round(c*1000*abs(m.sx))))
    return [((u-m.ox)/m.sx/1000,(v-m.oy)/m.sy/1000) for v in range(0,m.H,step) for u in range(0,m.W,step)
            if m.free((u-m.ox)/m.sx,(v-m.oy)/m.sy)]
def register(c,angles,tx0,ty0,span):
    Sg={(round(x/c)+di,round(y/c)+dj) for x,y in free_cells(SAVED,c) for di in (-1,0,1) for dj in (-1,0,1)}
    Lc=free_cells(LIVE,c); n=int(round(span/c)); best=None
    for a in angles:
        fwd,_=rigid(a,0,0); R=[fwd(x,y) for x,y in Lc]
        for i in range(-n,n+1):
            for j in range(-n,n+1):
                tx,ty=tx0+i*c,ty0+j*c
                h=sum((round((x+tx)/c),round((y+ty)/c)) in Sg for x,y in R)/len(R)
                if best is None or h>best[0]: best=(h,a,tx,ty)
    return best
_,a,tx,ty=register(0.4,range(0,360,3),0,0,8)
live_ov,live_rot,live_tx,live_ty=register(0.1,[a+d*0.5 for d in range(-8,9)],tx,ty,0.6)
print(f"live -> saved: rotate {live_rot:.1f} deg, + ({live_tx:.2f}, {live_ty:.2f}) m, free-floor overlap {live_ov:.0%}")
DOCK=rigid(live_rot,live_tx,live_ty)[0](*DOCK_LIVE)

# --- step 2: saved -> floor, by room boxes ---------------------------------------------------
robot_rooms={r['name']:(r['x0']/1000,r['y0']/1000,r['x1']/1000,r['y1']/1000) for r in saved_state['rooms'].values()}
cfg=cached('config.yaml',lambda ha:ha.call('get_config_file',{'filename':'espresense/config.yaml'}))
first=cfg.split('- id: second')[0].split('- id: first')[1]
floor_rooms={m.group(1):[tuple(float(v) for v in p.split(',')) for p in re.findall(r'\[([^\]]+)\]',m.group(2))]
             for m in re.finditer(r'name:\s*(\w+)\s*\n\s*points:\s*\n((?:\s*- \[[^\]]+\]\s*\n)+)',first)}

def bbox(poly):
    xs,ys=zip(*poly); return min(xs),min(ys),max(xs),max(ys)
floor_boxes={n:bbox(p) for n,p in floor_rooms.items()}

def inside(x,y,poly):
    c=False
    for (x1,y1),(x2,y2) in zip(poly,poly[1:]+poly[:1]):
        if (y1>y)!=(y2>y) and x<(x2-x1)*(y-y1)/(y2-y1)+x1: c=not c
    return c
room_at=lambda x,y:next((n for n,p in floor_rooms.items() if inside(x,y,p)),None)

def place(x,y,rot,mir,tx,ty):
    y*=mir; c,s=round(math.cos(math.radians(rot))),round(math.sin(math.radians(rot)))
    return c*x-s*y+tx, s*x+c*y+ty
def place_box(b,rot,mir,tx,ty):
    (ax,ay),(bx,by)=place(b[0],b[1],rot,mir,tx,ty),place(b[2],b[3],rot,mir,tx,ty)
    return min(ax,bx),min(ay,by),max(ax,bx),max(ay,by)
def iou(a,b):
    ix=max(0,min(a[2],b[2])-max(a[0],b[0])); iy=max(0,min(a[3],b[3])-max(a[1],b[1]))
    i=ix*iy; return i/((a[2]-a[0])*(a[3]-a[1])+(b[2]-b[0])*(b[3]-b[1])-i)

def match(rot,mir,tx,ty):
    return {r:max(((iou(place_box(b,rot,mir,tx,ty),fb),n) for n,fb in floor_boxes.items())) for r,b in robot_rooms.items()}

# The coffee-table room must exist in the robot map; it is the one Darrell's rectangle sits in.
# Without the image's pixel calibration we identify it by elimination: the landmark constraint is
# that SOME robot room lands on Family, and the dock lands in the Foyer.
frange=lambda a,b,s:[a+i*s for i in range(int(round((b-a)/s))+1)]
cands=[]
for rot in (0,90,180,270):
    for mir in (1,-1):
        best=None
        for tx in frange(-12,24,0.25):
            for ty in frange(-12,24,0.25):
                if room_at(*place(*DOCK,rot,mir,tx,ty))!=DOCK_ROOM: continue
                m=match(rot,mir,tx,ty)
                if not any(n==TABLE_ROOM and v>0.3 for v,n in m.values()): continue
                s=sum(v for v,_ in m.values())
                if best is None or s>best[0]: best=(s,tx,ty)
        if best: cands.append((best[0],rot,mir,best[1],best[2]))
cands.sort(reverse=True)
if not cands: sys.exit("no orientation satisfies the landmark constraints")

# Refine the winner's translation on a fine grid.
_,rot,mir,tx0,ty0=cands[0]
s,tx,ty=max(((sum(v for v,_ in match(rot,mir,tx,ty).values()),tx,ty)
             for tx in frange(tx0-0.4,tx0+0.4,0.05) for ty in frange(ty0-0.4,ty0+0.4,0.05)
             if room_at(*place(*DOCK,rot,mir,tx,ty))==DOCK_ROOM))

print("orientations satisfying dock-in-Foyer and a Family match (summed room IoU):")
for c in cands: print(f"  rot {c[1]:3d} mirror {('no','yes')[c[2]<0]:3}  t=({c[3]:6.2f},{c[4]:6.2f})  IoU sum {c[0]:.2f}")
print(f"\nsaved -> floor: floor = R({rot}) @ ({'' if mir>0 else 'mirror_y '}robot) + ({tx:.2f}, {ty:.2f})   IoU sum {s:.2f}")
print("\nrobot room -> floorplan room (IoU):")
for r,(v,n) in sorted(match(rot,mir,tx,ty).items()):
    print(f"  {r:<7} -> {n:<8} {v:.2f}")
dx,dy=place(*DOCK,rot,mir,tx,ty); print(f"\ndock -> ({dx:.2f},{dy:.2f}) in {room_at(dx,dy)}")

assert mir>0, "a mirrored solution needs frames.py to grow a mirror term"
r4=lambda v:round(v,4)
json.dump({'liveToSaved':{'rotDeg':r4(live_rot),'tx':r4(live_tx),'ty':r4(live_ty),'overlap':r4(live_ov)},
           'savedToFloor':{'rotDeg':rot,'tx':r4(tx),'ty':r4(ty),'iouSum':r4(s)}},
          open(os.path.join(HERE,'frames.json'),'w'),indent=1)
from frames import load
F=load(); print("wrote frames.json")

fx=json.load(open(FIXTURE))
print("\nsurvey points (live frame -> floor):")
for p in fx['points']:
    x,y=F['live_to_floor'](p['robotX'],p['robotY'])
    print(f"  {p['label']:<16} -> ({x:5.2f},{y:5.2f})  {room_at(x,y) or 'not in any room polygon'}")

# Cross-check ONLY: how well do the measured ranges agree with this geometry-derived placement?
nodes={n['id']:(n['x'],n['y'],n['z']) for n in fx['nodes']}
res=[math.log(math.dist((*F['live_to_floor'](p['robotX'],p['robotY']),0.15),nodes[nid]))-math.log(o['dist'])
     for p in fx['points'] for nid,o in p['m'].items() if nid in nodes and o['dist']>0]
k=sorted(res)[len(res)//2]
print(f"\ncross-check (not used): median range inflation x{math.exp(-k):.2f}, "
      f"rms log residual {math.sqrt(sum((r-k)**2 for r in res)/len(res)):.3f} over {len(res)} ranges")
