# Robot survey rig

Drives a Dreame P10 Pro Ultra with an ESPresense `calibrate` node strapped to it, parks it at a
list of waypoints, and records every node's view of it. The robot's LiDAR gives ground truth to
a few millimetres, which is what makes the resulting fixtures worth asserting against.

## Frames

The robot reports position in **its own live frame**. Most assertions in `RobotSurveyTests` use only
rotation- and translation-invariant quantities, so they cannot be skewed by a transform error.

The `Accuracy_` tests need floorplan coordinates. `frames.json` (fitted by `fit_frame.py`, helpers in
`frames.py`) maps live -> saved map -> floorplan using geometry alone: the robot's map against the
Companion room polygons, about 0.2 m 1-sigma. It must NOT be fitted from RSSI -- that would launder
the very errors we are trying to measure into the ground truth. As a check that is not a fit, the
strongest node at each point is usually one of the nearest under this transform, and measured vs true
node distance correlates at 0.62; every rotated or mirrored alternative does worse.

`build_fixture.py` writes `floorX`/`floorY`/`floorRoom` per point from it. Node positions and room
polygons come from the Companion config (`ESPRESENSE_CONFIG`, e.g. the copy `fit_frame.py` caches
from Home Assistant); only those are copied into the fixture.

## The beacon can fall off

The `calibrate` node is a hat taped on top of the robot. On 2026-10-13 it came off, almost certainly
during a manual clean at p04, and the robot toured 18 more positions without it. Every node's RSSI then
stayed within ~1 dB while the robot moved up to 15 m, and the locators looked as if they had collapsed.
They hadn't: the beacon was lying still.

- `run.py` now aborts if the robot moved 2 m+ and the RSSI fingerprint changed by less than 2 dB.
- `build_fixture.py` drops captures from `BEACON_LOST_AT` on; they stay in `captures/`.
- `BeaconMovesWithRobot` fails on any fixture whose fingerprints don't change with position.

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
