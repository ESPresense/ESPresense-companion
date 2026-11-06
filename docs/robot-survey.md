# Survey with a robot

The fingerprint locator is only as good as its survey. On the robot survey that shipped with it, spots
with a surveyed neighbour within a metre located to 0.7-0.9 m; the few without one to 1.6 m. A home
changes, too: furniture moves, a door stays open, and a survey taken once drifts.

A robot vacuum with LiDAR knows where it is to a few millimetres, and it covers the floor on its own,
every week. Tape an ESPresense node (or any BLE beacon Companion tracks) to it, tell Companion where the
robot's pose comes from, and every cleaning run becomes a survey: the map grows denser and refreshes
itself, and the robot's own fix against its LiDAR pose is a live measurement of the whole pipeline.

```yaml
survey:
  robot:
    enabled: true
    device: "node:calibrate"          # the beacon, as Companion names it
    floor: first                      # the floor the robot cleans
    height: 0.15                      # beacon height above the floor (m)
    pose:
      entity: camera.p10_pro_ultra_map  # Home Assistant entity whose attributes carry the pose
      attribute: vacuum_position        # {x, y, a}: robot units, heading in degrees
      poll_secs: 2
    transform:                        # robot frame -> floorplan metres (see below)
      scale: 0.001                    # millimetres
      rotation_deg: 0
      offset: [0, 0]
      flip_y: false
    stationary_m: 0.2                 # poses within this over dwell_secs count as standing still
    dwell_secs: 6
    max_dwell_secs: 90                # standing still longer than this means parked: nothing is recorded
    moving: true                      # also record one-second bins while driving a straight line
    max_speed: 1.0                    # m/s; faster and a bin's interpolated position is not trusted
    max_fingerprints: 20000           # oldest robot samples are dropped past this; taught samples never
```

## The pose

As a Home Assistant add-on, Companion reads the entity through the Supervisor. Outside the add-on set
`HASS_URL` and `HASS_TOKEN` in its environment. The Dreame integration exposes the pose as the map
camera's `vacuum_position` attribute, in millimetres with the heading in `a`; other integrations differ,
and `attribute` names whichever one holds `{x, y}`. Where no integration exposes it, a small bridge can
`POST /api/survey/pose` with `{"x": ..., "y": ..., "heading": ...}` in the robot's own units.

A robot that relocalises or switches maps reports poses in a different frame. If the entity has an
attribute naming the map, set `map_attribute` and surveying pauses when it changes.

## The transform

Robot coordinates become floorplan metres as `rotate(scale * (x, flip_y ? -y : y), rotation_deg) + offset`.
Fit it from geometry, never from the radio: park the robot directly under two nodes you can see, read
its pose each time, and solve for rotation and offset from the two pairs (scale is the robot's unit).
A transform fitted from RSSI would launder the radio's errors into the survey's ground truth.

## What gets recorded

Where the robot stands still for `dwell_secs`, a complete fingerprint: each node's mean raw rssi over the
dwell, with the nodes that reported the beacon in the last two minutes but not during the dwell listed
as silent. A long pause yields one per dwell period until `max_dwell_secs`, after which the robot is
taken to be parked (on its dock, say) and nothing more is recorded until it moves.

Where it drives, one-second bins. The robot's pose arrives every few seconds, so a bin is placed by
interpolating the poses either side of it, and only when the robot was below `max_speed` and the pose
before those lies on the same line: a bin in the middle of a turn would be placed where the robot never
was. A bin only knows the nodes that reported in it; the surface treats the others as unknown, not
silent, so sparse bins add to the map without claiming anything about nodes they did not hear.

Samples are appended in batches and the file is pruned to `max_fingerprints`, oldest robot samples
first. Taught samples (the **I'm here** button) are never pruned.

The beacon rides at floor height while devices live at about a metre; a light mast lifts it, and lifts it
off the robot's own motors and battery. Height matters most near nodes.

## Watching it work

`GET /api/survey/status` (and the `get_survey_status` MCP tool) report the pose source and its age, how
many dwell and moving samples the run produced, what has been appended and pruned, and any warnings.
`GET /api/state/accuracy` reports the ruler: see [accuracy.md](accuracy.md).

## Where it cannot go

A robot surveys one floor, at floor height, where it can drive. Rooms it cannot reach, the garage
beyond its map, and the other floors are surveyed by hand with **I'm here**, or by carrying the robot up
and giving it a map of its own.
