"""Robot frames, as fitted by fit_frame.py into frames.json. All coordinates in metres.

  live  -- what vacuum_position / vacuum_goto use today (camera.p10_pro_ultra_map)
  saved -- the robot's saved 'Downstairs' map (camera.p10_pro_ultra_map_1), which has room boxes
  floor -- the Companion floorplan

  saved = R(live.rot) @ live + live.t        floor = R(saved.rot) @ saved + saved.t
"""
import json, math, os

HERE=os.path.dirname(os.path.abspath(__file__))
PATH=os.path.join(HERE,'frames.json')

def rigid(rot,tx,ty):
    c,s=math.cos(math.radians(rot)),math.sin(math.radians(rot))
    fwd=lambda x,y:(c*x-s*y+tx, s*x+c*y+ty)
    inv=lambda x,y:(c*(x-tx)+s*(y-ty), -s*(x-tx)+c*(y-ty))
    return fwd,inv

def load():
    f=json.load(open(PATH))
    l2s,s2l=rigid(f['liveToSaved']['rotDeg'],f['liveToSaved']['tx'],f['liveToSaved']['ty'])
    s2f,f2s=rigid(f['savedToFloor']['rotDeg'],f['savedToFloor']['tx'],f['savedToFloor']['ty'])
    return {'live_to_saved':l2s,'saved_to_live':s2l,'saved_to_floor':s2f,'floor_to_saved':f2s,
            'live_to_floor':lambda x,y:s2f(*l2s(x,y)),'floor_to_live':lambda x,y:s2l(*f2s(x,y))}
