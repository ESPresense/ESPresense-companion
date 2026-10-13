"""Turn the tour captures into a test fixture.

Positions are kept in the robot's live LiDAR frame (robotX/robotY). When frames.json exists, each
point also gets floorX/floorY/floorRoom from the geometry-only transform fitted by fit_frame.py --
never from RSSI, or the locator errors we measure would leak into the ground truth.

Node positions and room polygons come from the Companion config. Set ESPRESENSE_CONFIG to point at
it (fit_frame.py caches a copy fetched from Home Assistant); only those two things are copied into
the fixture, nothing else from the config.
"""
import json, re, os, sys, statistics

HERE=os.path.dirname(os.path.abspath(__file__))
CAPDIR=os.environ.get('SURVEY_OUT') or os.path.join(HERE,'captures')
FIXTURE=os.path.join(HERE,'../../tests/ESPresense.Companion.Tests/TestData/robot-survey-2026-10-13.json')
CFG=os.environ.get('ESPRESENSE_CONFIG') or os.path.expanduser('~/.espresense/config.yaml')

def slug(name): return re.sub(r'[^a-z0-9]+','_',name.lower()).strip('_')

# --- nodes and rooms from the Companion config ---------------------------------
nodes={}; rooms=[]; bounds={'first':[[0,0,0],[17,18,3]],'second':[[0,0,3],[17,18,5.5]]}
if os.path.exists(CFG):
    import yaml
    cfg=yaml.safe_load(open(CFG))
    for n in cfg.get('nodes') or []:
        if not n.get('point'): continue
        x,y,z=[float(v) for v in n['point']]
        nodes[slug(n['name'])]={'id':slug(n['name']),'x':x,'y':y,'z':z,'floors':list(n.get('floors') or [])}
    for fl in cfg.get('floors') or []:
        if fl.get('bounds'): bounds[fl['id']]=fl['bounds']
        for r in fl.get('rooms') or []:
            rooms.append({'id':slug(r['name']),'floor':fl['id'],'points':r['points']})
elif os.path.exists(FIXTURE):
    print(f"  {CFG} missing; nodes and rooms from {os.path.basename(FIXTURE)}", file=sys.stderr)
    old=json.load(open(FIXTURE))
    nodes={n['id']:n for n in old['nodes']}; rooms=old.get('rooms',[]); bounds=old.get('floorBounds',bounds)
else:
    sys.exit(f"no config at {CFG}; set ESPRESENSE_CONFIG")

# --- robot frame -> floorplan ---------------------------------------------------
to_floor=None
if os.path.exists(os.path.join(HERE,'frames.json')):
    sys.path.insert(0,HERE)
    import frames
    to_floor=frames.load()['live_to_floor']

def inside(x,y,poly):
    c=False
    for (x1,y1),(x2,y2) in zip(poly,poly[1:]+poly[:1]):
        if (y1>y)!=(y2>y) and x<(x2-x1)*(y-y1)/(y2-y1)+x1: c=not c
    return c
room_at=lambda x,y:next((r['id'] for r in rooms if r['floor']=='first' and inside(x,y,r['points'])),None)

TOPIC=re.compile(r'espresense/devices/node:calibrate/(\S+)')

# The calibrate hat came off the robot during the 2026-10-13 survey, almost certainly in the manual
# clean that started at p04 (robot pushing under Kitchen furniture). From p10 on, every node's mean RSSI
# stays within ~1 dB at every point while the robot moved up to 15 m: the beacon was lying still near
# p04. p04 itself is kept -- its fingerprint is 5 dB away from p03's 1.9 m off, so the beacon was still
# riding. Later captures do not measure the robot's position and are left out; they stay in captures/
# as a record (and as a static-beacon set). run.py now aborts a tour when this happens.
BEACON_LOST_AT=1791907572   # unix time p10's capture started, first after the clean
def capture_start(path):
    with open(path, errors='replace') as f: return float(f.readline().split(' ',1)[0])

def summarise(path):
    per={}
    for line in open(path, errors='replace'):
        parts=line.split(' ',2)
        if len(parts)<3: continue
        m=TOPIC.fullmatch(parts[1])
        if not m: continue
        try: j=json.loads(parts[2])
        except Exception: continue
        if 'distance' not in j or 'rssi' not in j: continue
        per.setdefault(m.group(1),[]).append((float(j['rssi']), float(j['distance'])))
    out={}
    for nid,vals in per.items():
        if nid not in nodes: continue          # unplaced node, no ground truth position
        if len(vals) < 3: continue             # too few hits in the dwell to mean anything
        r=[v[0] for v in vals]; d=[v[1] for v in vals]
        out[nid]={'rssi':round(statistics.fmean(r),2),
                  'dist':round(statistics.fmean(d),3),
                  'n':len(vals),
                  'sd':round(statistics.stdev(r),2) if len(r)>1 else 0.0}
    return out

tour=json.load(open(f'{CAPDIR}/tour.json'))
points=[]
for t in tour:
    if t['status']!='ok': continue
    cap=os.path.join(CAPDIR, os.path.basename(t['capture']))
    if capture_start(cap)>=BEACON_LOST_AT:
        print(f"  skip {t['label']}: beacon was off the robot", file=sys.stderr); continue
    m=summarise(cap)
    if len(m)<4:
        print(f"  skip {t['label']}: only {len(m)} placed nodes heard", file=sys.stderr); continue
    pb=t['pos_before']
    p={'label':t['label'],
       'robotX':round(pb[0]/1000,4),'robotY':round(pb[1]/1000,4),
       'headingDeg':pb[2],'driftMm':round(t['drift_mm'],1),'m':m}
    if to_floor:
        fx,fy=to_floor(pb[0]/1000,pb[1]/1000)
        p.update(floorX=round(fx,3),floorY=round(fy,3),floorRoom=room_at(fx,fy))
    points.append(p)

used=sorted({n for p in points for n in p['m']})
fixture={'frame':'robotX/robotY: robot live LiDAR frame, metres. floorX/floorY: the same point in the '
                 'floorplan frame via tools/robot-survey/frames.json, a geometry-only fit of the robot map '
                 'to the room polygons (never RSSI). Node x/y/z are floorplan metres.',
         'headings':'One heading per position. This survey trades heading coverage for spatial '
                    'coverage; robot-survey-2026-10-12.json is the reverse (4 positions, 18 headings '
                    'at one of them). Heading alone moves inferred distance by up to 2.9x, so do not '
                    'read a single-heading point as the typical reading for that spot.',
         'captured':'2026-10-13',
         'nodes':[nodes[n] for n in used],
         'rooms':rooms,
         'points':points,
         'floorBounds':bounds}
json.dump(fixture, open(FIXTURE,'w'), indent=1)
print(f"{len(points)} points, {len(used)} placed nodes, {len(rooms)} rooms, floor coords {'yes' if to_floor else 'no'}")
for p in points:
    fl=f"  floor ({p['floorX']:>6.2f},{p['floorY']:>6.2f}) {p['floorRoom'] or '-'}" if to_floor else ''
    print(f"  {p['label']:<22} ({p['robotX']:>7.3f},{p['robotY']:>7.3f}) hdg {p['headingDeg']:>3}  "
          f"{len(p['m']):>2} nodes  drift {p['driftMm']:>5.1f}mm{fl}")
