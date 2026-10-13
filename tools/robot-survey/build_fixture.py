"""Turn the tour captures into a test fixture. Robot frame, no transform applied."""
import json, re, os, sys, math, statistics, glob

HERE=os.path.dirname(os.path.abspath(__file__))
CAPDIR=os.environ.get('SURVEY_OUT') or os.path.join(HERE,'captures')
FIXTURE=os.path.join(HERE,'../../tests/ESPresense.Companion.Tests/TestData/robot-survey-2026-10-13.json')
CFG=os.path.expanduser('~/.espresense/config.yaml')

def slug(name): return re.sub(r'[^a-z0-9]+','_',name.lower()).strip('_')

# --- node coordinates straight out of the live config -------------------------
nodes={}
if not os.path.exists(CFG) and os.path.exists(FIXTURE):
    # No live config on this machine: reuse the coordinates the last build took from it.
    print(f"  {CFG} missing; node coordinates from {os.path.basename(FIXTURE)}", file=sys.stderr)
    nodes={n['id']:n for n in json.load(open(FIXTURE))['nodes']}
else:
  blk=re.search(r'^nodes:\s*$(.*?)(?=^\S|\Z)', open(CFG).read(), re.M|re.S).group(1)
  for m in re.finditer(r'-\s+name:\s*(.+?)\n(.*?)(?=\n\s*-\s+name:|\Z)', blk, re.S):
      name=m.group(1).strip(); body=m.group(2)
      pt=re.search(r'point:\s*\[([^\]]+)\]', body)
      fl=re.search(r'floors:\s*\[([^\]]+)\]', body)
      if not pt: continue
      x,y,z=[float(v) for v in pt.group(1).split(',')]
      floors=[f.strip().strip('"\'') for f in fl.group(1).split(',')] if fl else []
      nodes[slug(name)]={'id':slug(name),'x':x,'y':y,'z':z,'floors':floors}

# 'basement' is a placed node that lives in the Companion's own store, not config.yaml.
nodes['basement']={'id':'basement','x':15.8,'y':11.5,'z':-0.5,'floors':['first']}

TOPIC=re.compile(r'espresense/devices/node:calibrate/(\S+)')

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
    m=summarise(os.path.join(CAPDIR, os.path.basename(t['capture'])))
    if len(m)<4: 
        print(f"  skip {t['label']}: only {len(m)} placed nodes heard", file=sys.stderr); continue
    pb=t['pos_before']
    points.append({'label':t['label'],
                   'robotX':round(pb[0]/1000,4),'robotY':round(pb[1]/1000,4),
                   'headingDeg':pb[2],'driftMm':round(t['drift_mm'],1),'m':m})

used=sorted({n for p in points for n in p['m']})
fixture={'frame':'Robot LiDAR frame, metres. NOT the floorplan frame: no transform has been '
                 'established. Node x/y/z ARE floorplan metres. Only frame-free assertions are valid.',
         'headings':'One heading per position. This survey trades heading coverage for spatial '
                    'coverage; robot-survey-2026-10-12.json is the reverse (4 positions, 18 headings '
                    'at one of them). Heading alone moves inferred distance by up to 2.9x, so do not '
                    'read a single-heading point as the typical reading for that spot.',
         'captured':'2026-10-13',
         'nodes':[nodes[n] for n in used],
         'points':points,
         'floorBounds':{'first':[[0,0,0],[17,18,3]],'second':[[0,0,3],[17,18,5.5]]}}
json.dump(fixture, open(FIXTURE,'w'), indent=1)
print(f"{len(points)} points, {len(used)} placed nodes")
for p in points:
    print(f"  {p['label']:<22} ({p['robotX']:>7.3f},{p['robotY']:>7.3f}) hdg {p['headingDeg']:>3}  "
          f"{len(p['m']):>2} nodes  drift {p['driftMm']:>5.1f}mm")
