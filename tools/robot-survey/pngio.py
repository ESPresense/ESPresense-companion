"""Minimal RGBA PNG read/write: no PIL on the survey machine."""
import struct, zlib

def read_png(path):
    d=open(path,'rb').read(); assert d[:8]==b'\x89PNG\r\n\x1a\n'
    i=8; idat=b''; W=H=None
    while i<len(d):
        n,t=struct.unpack('>I4s',d[i:i+8]); c=d[i+8:i+8+n]; i+=12+n
        if t==b'IHDR':
            W,H,bd,ct=struct.unpack('>IIBB',c[:10]); assert bd==8 and ct==6, 'only 8-bit RGBA'
        elif t==b'IDAT': idat+=c
    raw=zlib.decompress(idat); bpp=4; stride=W*bpp; out=bytearray(H*stride); prev=bytearray(stride)
    for y in range(H):
        f=raw[y*(stride+1)]; line=bytearray(raw[y*(stride+1)+1:(y+1)*(stride+1)])
        if f==1:
            for x in range(bpp,stride): line[x]=(line[x]+line[x-bpp])&255
        elif f==2:
            for x in range(stride): line[x]=(line[x]+prev[x])&255
        elif f==3:
            for x in range(stride): line[x]=(line[x]+((line[x-bpp] if x>=bpp else 0)+prev[x])//2)&255
        elif f==4:
            for x in range(stride):
                a=line[x-bpp] if x>=bpp else 0; b=prev[x]; c=prev[x-bpp] if x>=bpp else 0
                p=a+b-c; pa,pb,pc=abs(p-a),abs(p-b),abs(p-c)
                line[x]=(line[x]+(a if pa<=pb and pa<=pc else b if pb<=pc else c))&255
        out[y*stride:(y+1)*stride]=line; prev=line
    return W,H,out

def write_png(path, W, H, rgb):
    """rgb: bytearray of W*H*3."""
    raw=b''.join(b'\x00'+bytes(rgb[y*W*3:(y+1)*W*3]) for y in range(H))
    chunk=lambda t,c: struct.pack('>I',len(c))+t+c+struct.pack('>I',zlib.crc32(t+c)&0xffffffff)
    open(path,'wb').write(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',W,H,8,2,0,0,0))
                          +chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b''))

class SavedMap:
    """The robot's saved map image with its calibration: robot mm <-> image px."""
    def __init__(self, png, calib):
        self.W,self.H,self.px=read_png(png)
        (o,ax,ay)=[(c['vacuum'],c['map']) for c in calib]
        self.ox,self.oy=o[1]['x'],o[1]['y']
        self.sx=(ax[1]['x']-self.ox)/1000; self.sy=(ay[1]['y']-self.oy)/1000
    def to_px(self,X,Y): return self.ox+X*self.sx, self.oy+Y*self.sy
    def rgba(self,X,Y):
        u,v=(int(round(c)) for c in self.to_px(X,Y))
        if not(0<=u<self.W and 0<=v<self.H): return None
        i=(v*self.W+u)*4; return tuple(self.px[i:i+4])
    def free(self,X,Y):
        p=self.rgba(X,Y)
        if not p or p[3]<128: return False
        r,g,b,_=p; return not (abs(r-g)<10 and abs(g-b)<10 and r<200)   # grey = wall/obstacle
