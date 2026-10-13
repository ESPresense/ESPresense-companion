"""Draw the robot's saved map warped into the floorplan frame, with the Companion rooms, placed
nodes, dock, captured survey points and planned anchor waypoints on top. y is up, as in the
Companion UI (flip_y defaults to true). Writes overlay.png (pure Python, no PIL)."""
import json, math, os, re, sys
from pngio import SavedMap, write_png
from frames import load

HERE=os.path.dirname(os.path.abspath(__file__))
CACHE=os.environ.get('FIT_CACHE','/tmp/fitframe')
OUT=sys.argv[1] if len(sys.argv)>1 else os.path.join(CACHE,'overlay.png')
FR=load(); FR_json=json.load(open(os.path.join(HERE,'frames.json')))
to_floor=lambda X,Y:FR['live_to_floor'](X/1000,Y/1000)                 # live-frame mm -> floor m
to_saved=lambda fx,fy:tuple(v*1000 for v in FR['floor_to_saved'](fx,fy))  # floor m -> saved-map mm

S=50; X0,X1,Y0,Y1=-1.5,18,-1.5,19; TOP=90
W=int((X1-X0)*S); H=TOP+int((Y1-Y0)*S)
img=bytearray(b'\xff'*W*H*3)
uv=lambda fx,fy:(int(round((fx-X0)*S)), TOP+int(round((Y1-fy)*S)))

def put(u,v,c):
    if 0<=u<W and 0<=v<H: img[(v*W+u)*3:(v*W+u)*3+3]=bytes(c)
def disk(u,v,r,c):
    for dv in range(-r,r+1):
        for du in range(-r,r+1):
            if du*du+dv*dv<=r*r: put(u+du,v+dv,c)
def line(a,b,c,w=1):
    (u0,v0),(u1,v1)=a,b; n=max(abs(u1-u0),abs(v1-v0),1)
    for i in range(n+1): disk(round(u0+(u1-u0)*i/n),round(v0+(v1-v0)*i/n),w,c)

F={'A':'0E11111F111111','B':'1E11111E11111E','C':'0E11101010110E','D':'1E11111111111E','E':'1F10101E10101F',
   'F':'1F10101E101010','G':'0E11101711110F','H':'1111111F111111','I':'0E04040404040E','J':'07020202021 20C'.replace(' ',''),
   'K':'11121418141211','L':'1010101010101F','M':'111B1515111111','N':'11111915131111','O':'0E11111111110E',
   'P':'1E11111E101010','Q':'0E11111115120D','R':'1E11111E141211','S':'0F10100E01011E','T':'1F040404040404',
   'U':'1111111111110E','V':'1111111111 0A04'.replace(' ',''),'W':'1111111515150A','X':'11110A040A1111','Y':'1111110A040404',
   'Z':'1F01020408101F','0':'0E111315191 10E'.replace(' ',''),'1':'040C040404040E','2':'0E110102040 81F'.replace(' ',''),
   '3':'1F020402011 10E'.replace(' ',''),'4':'02060A121F0202','5':'1F101E0101110E','6':'0608101E11110E','7':'1F010204080808',
   '8':'0E11110E11110E','9':'0E11110F01020C',' ':'00000000000000','.':'0000000000 0C0C'.replace(' ',''),'-':'0000001F000000',
   '(':'02040808080402',')':'08040202020408',',':'00000000 0C0408'.replace(' ',''),':':'000C0C000C0C00','/':'01010204081010',
   '=':'00001F001F0000','+':'0004041F040400'}
def text(u,v,s,c,k=2,bg=None):
    s=s.upper(); w=len(s)*6*k
    if bg:
        for dv in range(-k,7*k+k):
            for du in range(-k,w+k): put(u+du,v+dv,bg)
    for i,ch in enumerate(s):
        g=F.get(ch,F[' '])
        for r in range(7):
            bits=int(g[2*r:2*r+2],16)
            for col in range(5):
                if bits>>(4-col)&1:
                    for a in range(k):
                        for b in range(k): put(u+i*6*k+col*k+a, v+r*k+b, c)
    return w

# 1. robot saved map, warped
m=SavedMap(os.path.join(HERE,'maps/saved.png'), json.load(open(os.path.join(HERE,'maps/saved.json')))['calibration_points'])
for v in range(TOP,H):
    for u in range(W):
        p=m.rgba(*to_saved(X0+u/S, Y1-(v-TOP)/S))
        if p and p[3]>=128:
            r,g,b,_=p
            c=(150,150,150) if abs(r-g)<10 and abs(g-b)<10 and r<200 else tuple(int(255-(255-x)*0.45) for x in (r,g,b))
            img[(v*W+u)*3:(v*W+u)*3+3]=bytes(c)

# 2. floorplan rooms (first floor solid, second floor faint)
cfg=open(f'{CACHE}/config.yaml').read()
first,second=cfg.split('- id: first')[1].split('- id: second')
second=second.split('\nnodes:')[0]
rx=r'name:\s*([\w ]+?)\s*\n\s*points:\s*\n((?:\s*- \[[^\]]+\]\s*\n)+)'
for blk,col,w in ((second,(200,170,220),1),(first,(30,30,30),2)):
    for mm in re.finditer(rx,blk):
        pts=[tuple(float(x) for x in q.split(',')) for q in re.findall(r'\[([^\]]+)\]',mm.group(2))]
        for a,b in zip(pts,pts[1:]+pts[:1]): line(uv(*a),uv(*b),col,w)
        if blk is first:
            cx,cy=sum(p[0] for p in pts)/len(pts),sum(p[1] for p in pts)/len(pts); u,v=uv(cx,cy)
            text(u-len(mm.group(1))*6, v-7, mm.group(1), (30,30,30))

# 3. nodes
blk=re.search(r'^nodes:\s*$(.*?)(?=^\S|\Z)',cfg,re.M|re.S).group(1)
for n in re.finditer(r'-\s+name:\s*(.+?)\n\s+point:\s*\[([^\]]+)\]',blk):
    x,y,z=[float(t) for t in n.group(2).split(',')]; u,v=uv(x,y)
    col=(210,30,30) if z<3 else (140,60,190)
    disk(u,v,7,(255,255,255)); disk(u,v,5,col)
    text(u+9,v-6,n.group(1).strip(),col,2,bg=(255,255,255))

# 4. dock, captured points, planned anchors
u,v=uv(*to_floor(-274,329))
for du in range(-8,9):
    for dv in range(-8,9): put(u+du,v+dv,(0,0,0))
text(u+11,v+4,'DOCK',(0,0,0),2,bg=(255,255,255))
for t in json.load(open(os.path.join(HERE,'captures/tour.json'))):
    if t['status']!='ok': continue
    u,v=uv(*to_floor(*t['pos_before'][:2]))
    disk(u,v,8,(255,255,255)); disk(u,v,6,(20,150,60))
    text(u+10,v+6,t['label'][:3],(20,120,50),2,bg=(255,255,255))
ap=os.path.join(HERE,'anchor_waypoints.json')
for i,(X,Y,_) in enumerate(json.load(open(ap)) if os.path.exists(ap) else []):
    u,v=uv(*to_floor(X,Y))
    line((u-8,v-8),(u+8,v+8),(235,120,0),2); line((u-8,v+8),(u+8,v-8),(235,120,0),2)
    text(u+10,v-18,f'A{i+1}',(200,90,0),2,bg=(255,255,255))

# 5. title, legend, 1 m grid ticks
text(10,10,'ROBOT SAVED MAP IN FLOORPLAN FRAME (LIVE = SAVED ROTATED %.1f DEG)'%FR_json['liveToSaved']['rotDeg'],(0,0,0),2)
x=10
for lbl,col in (('ROBOT FREE FLOOR',(150,190,240)),('ROOMS',(30,30,30)),('NODE',(210,30,30)),('UPSTAIRS NODE',(140,60,190)),
                ('CAPTURED',(20,150,60)),('PLANNED',(235,120,0))):
    disk(x+6,47,6,col); x+=18+text(x+16,40,lbl,(0,0,0),2)+14
for g in range(0,18):
    u,v=uv(g,0); line((u,v),(u,v+6),(0,0,0),0); text(u-3,v+10,str(g),(90,90,90),1)
for g in range(0,19):
    u,v=uv(0,g); line((u-6,v),(u,v),(0,0,0),0); text(u-20,v-3,str(g),(90,90,90),1)
write_png(OUT,W,H,img); print('wrote',OUT,W,'x',H)
