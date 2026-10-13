# Robot survey rig

Drives a Dreame P10 Pro Ultra with an ESPresense `calibrate` node strapped to it, parks it at a
list of waypoints, and records every node's view of it. The robot's LiDAR gives ground truth to
a few millimetres, which is what makes the resulting fixtures worth asserting against.

## Why the fixtures are frame-free

The robot reports position in **its own frame**. No rigid transform into the floorplan frame has
been established, and it must NOT be fitted from RSSI -- that would launder the very errors we
are trying to measure into the ground truth. So every assertion in `RobotSurveyTests` uses only
rotation- and translation-invariant quantities.

To unlock absolute-accuracy tests, the transform has to come from geometry: park the robot
directly beneath two known nodes and have Darrell confirm. Two correspondences = 4 constraints
for 3 degrees of freedom.

## Running a survey

```
python3 run.py          # drives the tour, writes cap_*.txt + tour.json
python3 build_fixture.py   # folds them into robot-survey-<date>.json
```

`run.py` talks to Home Assistant over the MCP HTTP endpoint via `ha.py`; point `ha.URL` at
whatever endpoint the running session has. It captures with `mosquitto_sub` against
`mqtt.z13.org:1883`.

Things learned the hard way:

- The robot refuses `vacuum_goto` while it is in a paused "returning" state. Call `vacuum.stop`
  first -- `ensure_ready()` does this.
- `vacuum_goto` is cruise, not cleaning. HA's generic vacuum state still reads `cleaning`; check
  `status` (`cruising_point`) and `cleaned_area`. The tour logs a rise in `cleaned_area` but does not abort.
- Match the process name carefully when killing a tour. `pkill -f survey2/run.py` does not match
  a process started as `python3 run.py`, and two tours driving one robot fight each other.
- Unreachable waypoints are simply ignored by the robot; the tour detects "never moved" and skips.
- Only `calibrate`'s **receive** direction matters: `espresense/devices/node:calibrate/<rx>` is
  "node rx heard the robot", which is the direction the locators consume.
- Placed node coordinates are 13 nodes from `~/.espresense/config.yaml` plus `basement`, which
  lives in the Companion's own store. The rest of the nodes are unplaced and have no ground truth.

## The two fixtures are complements

- `robot-survey-2026-10-12.json` -- 4 positions, 18 headings at one of them. Good for orientation
  sensitivity, poor spatial spread.
- `robot-survey-2026-10-13.json` -- many positions, one heading each. The reverse.

Heading alone moves inferred distance by up to 2.9x, so never read a single-heading point as the
typical reading for that spot.
